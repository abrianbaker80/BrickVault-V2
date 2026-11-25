ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check CHECK (kind IN
    ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release',
     'stock-loss','recount','offer-withdrawal','quarantine','quarantine-release'));

CREATE TABLE brickvault.stock_quarantines (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    position_id uuid NOT NULL,
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    catalog_id uuid NOT NULL REFERENCES brickvault.catalog(id),
    catalog_revision integer NOT NULL CHECK (catalog_revision > 0),
    recorded_catalog jsonb NOT NULL,
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision > 0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    FOREIGN KEY (purchase_id,position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    UNIQUE (purchase_id,expected_purchase_revision)
);

CREATE TABLE brickvault.stock_quarantine_releases (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    position_id uuid NOT NULL,
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    catalog_id uuid NOT NULL REFERENCES brickvault.catalog(id),
    catalog_revision integer NOT NULL CHECK (catalog_revision > 0),
    recorded_catalog jsonb NOT NULL,
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision > 0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    quarantine_id uuid NOT NULL REFERENCES brickvault.stock_quarantines(id),
    inspection_result text NOT NULL CHECK (inspection_result='passed'),
    FOREIGN KEY (purchase_id,position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    UNIQUE (purchase_id,expected_purchase_revision)
);

CREATE INDEX stock_quarantines_position ON brickvault.stock_quarantines(position_id,expected_purchase_revision DESC);
CREATE INDEX stock_quarantine_releases_receipt ON brickvault.stock_quarantine_releases(quarantine_id);
CREATE INDEX stock_quarantine_releases_position ON brickvault.stock_quarantine_releases(position_id);

-- The review row records affirmative attribution, including an explicit zero quantity.
-- Prior v7-v9 physical-loss facts retain their original schema and data.
CREATE TABLE brickvault.stock_loss_quarantine_reviews (
    loss_id uuid PRIMARY KEY REFERENCES brickvault.stock_loss_postings(id),
    quarantined_quantity_before integer NOT NULL CHECK (quarantined_quantity_before BETWEEN 0 AND 1000000)
);

CREATE TABLE brickvault.stock_loss_quarantine_attributions (
    loss_id uuid NOT NULL REFERENCES brickvault.stock_loss_quarantine_reviews(loss_id),
    quarantine_id uuid NOT NULL REFERENCES brickvault.stock_quarantines(id),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    prior_remaining_quantity integer NOT NULL CHECK (prior_remaining_quantity BETWEEN quantity AND 1000000),
    PRIMARY KEY (loss_id,quarantine_id)
);

CREATE INDEX stock_loss_quarantine_attributions_receipt
    ON brickvault.stock_loss_quarantine_attributions(quarantine_id);

CREATE VIEW brickvault.current_stock_quarantines AS
SELECT q.*,inspected.quantity AS released_quantity,
    q.quantity-inspected.quantity-lost.quantity AS remaining_quantity,lost.quantity AS lost_quantity
FROM brickvault.stock_quarantines q
CROSS JOIN LATERAL (
    SELECT COALESCE(sum(r.quantity),0)::integer AS quantity
    FROM brickvault.stock_quarantine_releases r WHERE r.quarantine_id=q.id
) inspected
CROSS JOIN LATERAL (
    SELECT COALESCE(sum(a.quantity),0)::integer AS quantity
    FROM brickvault.stock_loss_quarantine_attributions a WHERE a.quarantine_id=q.id
) lost;

CREATE VIEW brickvault.current_position_quarantine AS
SELECT position_id,sum(remaining_quantity)::integer AS quantity
FROM brickvault.current_stock_quarantines GROUP BY position_id HAVING sum(remaining_quantity)>0;

CREATE FUNCTION brickvault.guard_stock_quarantine() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    stock brickvault.current_placement_stock%ROWTYPE;
    catalog brickvault.catalog%ROWTYPE;
    release_receipt boolean := TG_TABLE_NAME='stock_quarantine_releases';
    available integer;
BEGIN
    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='quarantine_revision_conflict'; END IF;

    SELECT * INTO stock FROM brickvault.current_placement_stock
    WHERE id=NEW.position_id AND purchase_id=NEW.purchase_id AND location_id=NEW.location_id AND quantity>0;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='quarantine_position_mismatch'; END IF;
    PERFORM 1 FROM brickvault.locations WHERE id=NEW.location_id AND code=NEW.location_code FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='quarantine_bin_conflict'; END IF;
    SELECT * INTO catalog FROM brickvault.catalog WHERE id=stock.catalog_id AND id=NEW.catalog_id
        AND revision=NEW.catalog_revision AND (NOT release_receipt OR integration_eligible) FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='quarantine_catalog_conflict'; END IF;
    NEW.recorded_catalog := jsonb_build_object(
        'id',catalog.id,'revision',catalog.revision,'itemType',catalog.item_type,'itemNumber',catalog.item_number,
        'variant',catalog.variant,'name',catalog.name,'colorId',catalog.color_id,'colorState',catalog.color_state,
        'condition',catalog.condition,'completeness',catalog.completeness,'source',catalog.source,
        'license',catalog.license,'mapping',catalog.mapping,'integrationEligible',catalog.integration_eligible);
    IF EXISTS (SELECT 1 FROM brickvault.stock_shortage_cases
               WHERE position_id=NEW.position_id AND resolved_by_command_id IS NULL) THEN
        RAISE check_violation USING CONSTRAINT='quarantine_shortage_open';
    END IF;

    IF release_receipt THEN
        SELECT remaining_quantity INTO available FROM brickvault.current_stock_quarantines
        WHERE id=NEW.quarantine_id AND position_id=NEW.position_id AND purchase_id=NEW.purchase_id;
        IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='unknown_quarantine_receipt'; END IF;
    ELSE
        SELECT stock.quantity
            -COALESCE((SELECT quantity FROM brickvault.current_position_quarantine WHERE position_id=NEW.position_id),0)
            -COALESCE((SELECT sum(quantity) FROM brickvault.current_offer_backing WHERE placement_id=NEW.position_id),0)
            -COALESCE((SELECT sum(remaining_quantity) FROM brickvault.current_build_holds WHERE position_id=NEW.position_id),0)
        INTO available;
    END IF;
    IF NEW.quantity>available THEN RAISE check_violation USING CONSTRAINT='quarantine_quantity_unavailable'; END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER guard_stock_quarantine BEFORE INSERT ON brickvault.stock_quarantines
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_stock_quarantine();

CREATE TRIGGER guard_stock_quarantine_release BEFORE INSERT ON brickvault.stock_quarantine_releases
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_stock_quarantine();

CREATE FUNCTION brickvault.require_stock_quarantine_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    required_kind text := CASE WHEN TG_TABLE_NAME='stock_quarantines' THEN 'quarantine' ELSE 'quarantine-release' END;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM brickvault.commands c JOIN brickvault.audit a USING (command_id)
                   WHERE c.command_id=NEW.command_id AND c.kind=required_kind AND c.entity_id=NEW.id
                     AND c.revision-1=NEW.expected_purchase_revision) THEN
        RAISE check_violation USING CONSTRAINT='quarantine_receipt_required';
    END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER require_quarantine_receipt AFTER INSERT ON brickvault.stock_quarantines
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_stock_quarantine_receipt();
CREATE CONSTRAINT TRIGGER require_quarantine_release_receipt AFTER INSERT ON brickvault.stock_quarantine_releases
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_stock_quarantine_receipt();

-- Existing offer, hold and transfer guards lock their purchase before changing capacity.
CREATE FUNCTION brickvault.require_quarantine_capacity() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    position uuid;
    quarantined integer;
BEGIN
    IF TG_TABLE_NAME='placement_transfers' THEN
        position := NEW.source_position_id;
    ELSIF TG_TABLE_NAME='offer_backing' THEN
        position := NEW.placement_id;
    ELSE
        position := NEW.position_id;
    END IF;
    SELECT quantity INTO quarantined FROM brickvault.current_position_quarantine WHERE position_id=position;
    IF FOUND AND quarantined
        + COALESCE((SELECT sum(quantity) FROM brickvault.current_offer_backing WHERE placement_id=position),0)
        + COALESCE((SELECT sum(remaining_quantity) FROM brickvault.current_build_holds WHERE position_id=position),0)
        > (SELECT quantity FROM brickvault.current_placement_stock WHERE id=position) THEN
        RAISE check_violation USING CONSTRAINT='quarantine_capacity_unavailable';
    END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER require_quarantine_capacity_on_hold AFTER INSERT ON brickvault.stock_build_holds
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_quarantine_capacity();
CREATE CONSTRAINT TRIGGER require_quarantine_capacity_on_offer AFTER INSERT ON brickvault.offer_backing
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_quarantine_capacity();
CREATE CONSTRAINT TRIGGER require_quarantine_capacity_on_transfer AFTER INSERT ON brickvault.placement_transfers
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_quarantine_capacity();

-- The loss insertion already advances and locks the purchase. Quarantine changes at
-- the open shortage's position remain fenced until this loss transaction completes.
CREATE FUNCTION brickvault.capture_stock_loss_quarantine_review() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    SELECT COALESCE(q.quantity,0) INTO NEW.quarantined_quantity_before
    FROM brickvault.stock_loss_postings l
    LEFT JOIN brickvault.current_position_quarantine q ON q.position_id=l.position_id
    WHERE l.id=NEW.loss_id AND NOT EXISTS (
        SELECT 1 FROM brickvault.commands c WHERE c.command_id=l.command_id);
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='stock_loss_quarantine_attribution_mismatch'; END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER capture_stock_loss_quarantine_review BEFORE INSERT ON brickvault.stock_loss_quarantine_reviews
    FOR EACH ROW EXECUTE FUNCTION brickvault.capture_stock_loss_quarantine_review();

CREATE FUNCTION brickvault.capture_stock_loss_quarantine_attribution() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    SELECT q.remaining_quantity INTO NEW.prior_remaining_quantity
    FROM brickvault.stock_loss_quarantine_reviews review
    JOIN brickvault.stock_loss_postings l ON l.id=review.loss_id
    JOIN brickvault.current_stock_quarantines q ON q.id=NEW.quarantine_id
        AND q.position_id=l.position_id AND q.purchase_id=l.purchase_id
    WHERE review.loss_id=NEW.loss_id AND NOT EXISTS (
        SELECT 1 FROM brickvault.commands c WHERE c.command_id=l.command_id);
    IF NOT FOUND OR NEW.quantity>NEW.prior_remaining_quantity THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_quarantine_attribution_mismatch';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER capture_stock_loss_quarantine_attribution BEFORE INSERT ON brickvault.stock_loss_quarantine_attributions
    FOR EACH ROW EXECUTE FUNCTION brickvault.capture_stock_loss_quarantine_attribution();

CREATE OR REPLACE FUNCTION brickvault.require_stock_loss_completion() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    required_reduction integer;
    actual_reduction bigint;
    prior_quarantine integer;
    attributed_loss bigint;
    remaining_quarantine integer;
    saleable_capacity integer;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.commands c JOIN brickvault.audit a USING (command_id)
        WHERE c.command_id=NEW.command_id AND c.kind='stock-loss' AND c.entity_id=NEW.id
          AND c.revision-1=NEW.expected_purchase_revision
    ) THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_receipt_required';
    END IF;

    SELECT quarantined_quantity_before INTO prior_quarantine
    FROM brickvault.stock_loss_quarantine_reviews WHERE loss_id=NEW.id;
    IF NOT FOUND THEN
        IF EXISTS (SELECT 1 FROM brickvault.current_position_quarantine WHERE position_id=NEW.position_id) THEN
            RAISE check_violation USING CONSTRAINT='stock_loss_quarantine_unresolved';
        END IF;
        prior_quarantine := 0;
    END IF;

    SELECT COALESCE(sum(quantity),0) INTO attributed_loss
    FROM brickvault.stock_loss_quarantine_attributions WHERE loss_id=NEW.id;
    IF attributed_loss>NEW.lost_quantity OR attributed_loss>prior_quarantine
        OR NEW.lost_quantity-attributed_loss>NEW.ledger_quantity_before-prior_quarantine THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_quarantine_attribution_mismatch';
    END IF;
    remaining_quarantine := prior_quarantine-attributed_loss;
    saleable_capacity := NEW.ledger_quantity_before-NEW.lost_quantity-NEW.protected_project_quantity-remaining_quarantine;
    IF saleable_capacity<0 THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_protected_shortfall';
    END IF;

    required_reduction := GREATEST(0,NEW.offer_backed_quantity_before-saleable_capacity);
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
