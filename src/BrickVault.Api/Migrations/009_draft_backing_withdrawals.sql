ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check CHECK (kind IN
    ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release',
     'stock-loss','recount','offer-withdrawal'));

CREATE TABLE brickvault.draft_backing_withdrawals (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    position_id uuid NOT NULL,
    offer_id uuid NOT NULL REFERENCES brickvault.offers(id),
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision>0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    prior_backed_quantity integer NOT NULL CHECK (prior_backed_quantity BETWEEN quantity AND 1000000),
    prior_total_offer_backed_quantity integer NOT NULL
        CHECK (prior_total_offer_backed_quantity BETWEEN prior_backed_quantity AND 1000000),
    FOREIGN KEY (purchase_id,position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    UNIQUE (purchase_id,expected_purchase_revision)
);

CREATE INDEX draft_backing_withdrawals_position_offer ON brickvault.draft_backing_withdrawals(position_id,offer_id);
CREATE INDEX draft_backing_withdrawals_offer ON brickvault.draft_backing_withdrawals(offer_id);

-- The only offer write port in this schema creates internal drafts. Qualify its original local journal and audit.
-- This says nothing about external inventory. A future binding/order ingress must extend this authority boundary.
CREATE VIEW brickvault.internal_draft_offers AS
SELECT o.id FROM brickvault.offers o
JOIN brickvault.commands c ON c.command_id=o.command_id AND c.kind='offer' AND c.entity_id=o.id
JOIN brickvault.audit a ON a.command_id=c.command_id;

CREATE VIEW brickvault.offer_backing_ledger AS
SELECT b.offer_id,b.placement_id,o.id AS event_id,o.command_id,'offer-backing'::text AS kind,
    b.quantity AS backing_delta,NULL::uuid AS related_position_id,NULL::text AS reason
FROM brickvault.offer_backing b JOIN brickvault.offers o ON o.id=b.offer_id
UNION ALL
SELECT b.offer_id,t.id,t.id,t.command_id,'transfer-in',b.quantity,t.source_position_id,t.reason
FROM brickvault.placement_transfer_backing b JOIN brickvault.placement_transfers t ON t.id=b.transfer_id
UNION ALL
SELECT b.offer_id,t.source_position_id,t.id,t.command_id,'transfer-out',-b.quantity,t.id,t.reason
FROM brickvault.placement_transfer_backing b JOIN brickvault.placement_transfers t ON t.id=b.transfer_id
UNION ALL
SELECT r.offer_id,l.position_id,l.id,l.command_id,'stock-loss',-r.quantity,NULL,l.reason
FROM brickvault.stock_loss_offer_reductions r JOIN brickvault.stock_loss_postings l ON l.id=r.loss_id
UNION ALL
SELECT w.offer_id,w.position_id,w.id,w.command_id,'offer-withdrawal',-w.quantity,NULL,w.reason
FROM brickvault.draft_backing_withdrawals w;

CREATE OR REPLACE VIEW brickvault.current_offer_backing AS
SELECT offer_id,placement_id,sum(backing_delta)::integer AS quantity
FROM brickvault.offer_backing_ledger
GROUP BY offer_id,placement_id
HAVING sum(backing_delta) <> 0;

CREATE FUNCTION brickvault.capture_draft_backing_withdrawal() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='draft_backing_revision_conflict'; END IF;

    PERFORM 1 FROM brickvault.current_placement_stock
    WHERE id=NEW.position_id AND purchase_id=NEW.purchase_id AND location_id=NEW.location_id;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='draft_backing_position_mismatch'; END IF;
    PERFORM 1 FROM brickvault.locations WHERE id=NEW.location_id AND code=NEW.location_code FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='draft_backing_bin_conflict'; END IF;
    IF EXISTS (SELECT 1 FROM brickvault.stock_shortage_cases
        WHERE position_id=NEW.position_id AND resolved_by_command_id IS NULL) THEN
        RAISE check_violation USING CONSTRAINT='draft_backing_shortage_open';
    END IF;

    PERFORM 1 FROM brickvault.offers o JOIN brickvault.placement_positions p ON p.id=NEW.position_id
    WHERE o.id=NEW.offer_id AND o.purchase_id=NEW.purchase_id AND o.catalog_id=p.catalog_id
        AND o.catalog_revision=p.catalog_revision
        AND EXISTS (SELECT 1 FROM brickvault.offer_backing_ledger b WHERE b.offer_id=o.id AND b.placement_id=p.id);
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='draft_backing_offer_mismatch'; END IF;
    PERFORM 1 FROM brickvault.internal_draft_offers WHERE id=NEW.offer_id;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='draft_backing_authority_unverified'; END IF;

    SELECT COALESCE(sum(quantity) FILTER (WHERE placement_id=NEW.position_id),0),COALESCE(sum(quantity),0)
    INTO NEW.prior_backed_quantity,NEW.prior_total_offer_backed_quantity
    FROM brickvault.current_offer_backing WHERE offer_id=NEW.offer_id;
    IF NEW.quantity>NEW.prior_backed_quantity THEN
        RAISE check_violation USING CONSTRAINT='draft_backing_quantity_unavailable';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER capture_draft_backing_withdrawal BEFORE INSERT ON brickvault.draft_backing_withdrawals
    FOR EACH ROW EXECUTE FUNCTION brickvault.capture_draft_backing_withdrawal();

CREATE FUNCTION brickvault.require_draft_backing_withdrawal_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM brickvault.commands c JOIN brickvault.audit a USING(command_id)
        WHERE c.command_id=NEW.command_id AND c.kind='offer-withdrawal' AND c.entity_id=NEW.id
            AND c.revision-1=NEW.expected_purchase_revision) THEN
        RAISE check_violation USING CONSTRAINT='draft_backing_receipt_required';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER require_draft_backing_withdrawal_receipt AFTER INSERT ON brickvault.draft_backing_withdrawals
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_draft_backing_withdrawal_receipt();

CREATE OR REPLACE FUNCTION brickvault.require_stock_loss_completion() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    required_reduction integer;
    actual_reduction bigint;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.commands c JOIN brickvault.audit a USING (command_id)
        WHERE c.command_id=NEW.command_id AND c.kind='stock-loss' AND c.entity_id=NEW.id
          AND c.revision-1=NEW.expected_purchase_revision
    ) THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_receipt_required';
    END IF;

    required_reduction := GREATEST(0,NEW.offer_backed_quantity_before
        -(NEW.ledger_quantity_before-NEW.lost_quantity-NEW.protected_project_quantity));
    SELECT COALESCE(sum(quantity),0) INTO actual_reduction
    FROM brickvault.stock_loss_offer_reductions WHERE loss_id=NEW.id;
    IF actual_reduction<>required_reduction THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_offer_reduction_mismatch';
    END IF;

    -- Read only the preceding purchase history. Later events in the same transaction cannot change the receipt.
    UPDATE brickvault.stock_loss_offer_reductions r SET prior_backed_quantity=s.prior
    FROM (
        SELECT chosen.offer_id,
            COALESCE(sum(b.backing_delta) FILTER (WHERE c.revision<=NEW.expected_purchase_revision),0)::integer AS prior
        FROM brickvault.stock_loss_offer_reductions chosen
        LEFT JOIN brickvault.offer_backing_ledger b
            ON b.offer_id=chosen.offer_id AND b.placement_id=NEW.position_id
        LEFT JOIN brickvault.commands c ON c.command_id=b.command_id
        WHERE chosen.loss_id=NEW.id
        GROUP BY chosen.offer_id
    ) s WHERE r.loss_id=NEW.id AND r.offer_id=s.offer_id;

    UPDATE brickvault.stock_shortage_cases SET resolved_by_command_id=NEW.command_id
    WHERE id=NEW.case_id AND position_id=NEW.position_id AND resolved_by_command_id IS NULL;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='stock_loss_case_conflict'; END IF;
    RETURN NULL;
END;
$$;
