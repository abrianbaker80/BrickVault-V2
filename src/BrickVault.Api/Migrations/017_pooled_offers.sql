-- An offer's origin purchase is its creation context. Each allocation keeps the purchase that
-- physically owns that position and the revision at which the operator committed the allocation.
ALTER TABLE brickvault.offer_backing ADD COLUMN purchase_revision integer CHECK (purchase_revision>0);
UPDATE brickvault.offer_backing b SET purchase_revision=c.revision
FROM brickvault.offers o JOIN brickvault.commands c USING(command_id) WHERE b.offer_id=o.id;
ALTER TABLE brickvault.offer_backing ALTER COLUMN purchase_revision SET NOT NULL;

DO $migration$
DECLARE old_fk name;
BEGIN
    SELECT conname INTO STRICT old_fk FROM pg_catalog.pg_constraint
    WHERE conrelid='brickvault.offer_backing'::regclass AND confrelid='brickvault.offers'::regclass AND contype='f';
    EXECUTE format('ALTER TABLE brickvault.offer_backing DROP CONSTRAINT %I', old_fk);
END;
$migration$;
ALTER TABLE brickvault.offers ADD UNIQUE (id,catalog_id,catalog_revision);
ALTER TABLE brickvault.offer_backing ADD CONSTRAINT offer_backing_identity_fk
    FOREIGN KEY (offer_id,catalog_id,catalog_revision) REFERENCES brickvault.offers(id,catalog_id,catalog_revision);

-- Relaxing the offer-side foreign key must not allow an unrelated purchase to become its origin.
CREATE OR REPLACE FUNCTION brickvault.require_offer_backing() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.offer_backing WHERE offer_id=NEW.id
        HAVING bool_or(purchase_id=NEW.purchase_id) AND count(*)<=64
    ) THEN
        RAISE check_violation USING CONSTRAINT='receiving_backing';
    END IF;
    RETURN NULL;
END;
$$;

-- guard_backing fences the source purchase before this trigger reads its revision.
CREATE FUNCTION brickvault.record_offer_source_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    SELECT revision INTO STRICT NEW.purchase_revision FROM brickvault.purchases WHERE id=NEW.purchase_id;
    RETURN NEW;
END;
$$;
CREATE TRIGGER record_offer_source_revision BEFORE INSERT ON brickvault.offer_backing
    FOR EACH ROW EXECUTE FUNCTION brickvault.record_offer_source_revision();

-- The old ceiling was the size of one purchase. An offer may have up to 64 original positions.
DO $migration$
DECLARE old_check name;
BEGIN
    SELECT c.conname INTO STRICT old_check FROM pg_catalog.pg_constraint c
    JOIN pg_catalog.pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=ANY(c.conkey)
    WHERE c.conrelid='brickvault.draft_backing_withdrawals'::regclass AND c.contype='c'
      AND a.attname='prior_total_offer_backed_quantity';
    EXECUTE format('ALTER TABLE brickvault.draft_backing_withdrawals DROP CONSTRAINT %I', old_check);
END;
$migration$;
ALTER TABLE brickvault.draft_backing_withdrawals
    ADD CONSTRAINT draft_backing_withdrawal_offer_total CHECK (prior_total_offer_backed_quantity BETWEEN prior_backed_quantity AND 64000000),
    ADD COLUMN expected_offer_total integer CHECK (expected_offer_total BETWEEN 0 AND 64000000);

-- A pooled offer has writers on more than one purchase. Touch a non-key field without
-- changing the price or its provenance. NO KEY UPDATE is compatible with the ledger's
-- foreign-key checks and gives stale repeatable-read writers a serialization failure.
CREATE FUNCTION brickvault.fence_pooled_offer_totals(targets uuid[]) RETURNS void LANGUAGE plpgsql
    SET search_path=pg_catalog AS $$
DECLARE target uuid;
BEGIN
    FOR target IN SELECT DISTINCT id FROM unnest(targets) ids(id) ORDER BY id LOOP
        UPDATE brickvault.offers o SET price_scale_verified=o.price_scale_verified WHERE o.id=target AND EXISTS (
            SELECT 1 FROM brickvault.offer_backing b WHERE b.offer_id=o.id AND b.purchase_id<>o.purchase_id
        );
    END LOOP;
END;
$$;
REVOKE ALL ON FUNCTION brickvault.fence_pooled_offer_totals(uuid[]) FROM PUBLIC;

CREATE OR REPLACE FUNCTION brickvault.capture_draft_backing_withdrawal() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='draft_backing_revision_conflict'; END IF;

    PERFORM 1 FROM brickvault.current_placement_stock
    WHERE id=NEW.position_id AND purchase_id=NEW.purchase_id AND location_id=NEW.location_id;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='draft_backing_position_mismatch'; END IF;
    NEW.location_code := brickvault.confirm_location_label(
        NEW.location_id,NEW.location_code,false,'draft_backing_bin_conflict');
    IF EXISTS (SELECT 1 FROM brickvault.stock_shortage_cases
        WHERE position_id=NEW.position_id AND resolved_by_command_id IS NULL) THEN
        RAISE check_violation USING CONSTRAINT='draft_backing_shortage_open';
    END IF;

    PERFORM 1 FROM brickvault.offers o JOIN brickvault.placement_positions p ON p.id=NEW.position_id
    WHERE o.id=NEW.offer_id AND o.catalog_id=p.catalog_id
        AND o.catalog_revision=p.catalog_revision
        AND EXISTS (SELECT 1 FROM brickvault.offer_backing_ledger b WHERE b.offer_id=o.id AND b.placement_id=p.id);
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='draft_backing_offer_mismatch'; END IF;
    PERFORM 1 FROM brickvault.internal_draft_offers WHERE id=NEW.offer_id;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='draft_backing_authority_unverified'; END IF;

    -- All operations that reduce the draft's global backing take this lock after the source purchase.
    PERFORM brickvault.fence_pooled_offer_totals(ARRAY[NEW.offer_id]);
    SELECT COALESCE(sum(quantity) FILTER (WHERE placement_id=NEW.position_id),0),COALESCE(sum(quantity),0)
    INTO NEW.prior_backed_quantity,NEW.prior_total_offer_backed_quantity
    FROM brickvault.current_offer_backing WHERE offer_id=NEW.offer_id;
    IF NEW.expected_offer_total IS NULL AND EXISTS (
        SELECT 1 FROM brickvault.offer_backing b JOIN brickvault.offers o ON o.id=b.offer_id
        WHERE b.offer_id=NEW.offer_id AND b.purchase_id<>o.purchase_id
    ) THEN RAISE check_violation USING CONSTRAINT='pooled_withdrawal_upgrade'; END IF;
    IF NEW.expected_offer_total IS NOT NULL AND NEW.expected_offer_total<>NEW.prior_total_offer_backed_quantity THEN
        RAISE check_violation USING CONSTRAINT='draft_backing_total_conflict';
    END IF;
    IF NEW.quantity>NEW.prior_backed_quantity THEN
        RAISE check_violation USING CONSTRAINT='draft_backing_quantity_unavailable';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.guard_stock_loss_reductions() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM loss_details r JOIN brickvault.stock_loss_postings l ON l.id=r.loss_id
        JOIN brickvault.commands c ON c.command_id=l.command_id
    ) THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_offer_reduction_mismatch';
    END IF;
    PERFORM brickvault.fence_pooled_offer_totals(ARRAY(SELECT offer_id FROM loss_details));
    RETURN NULL;
END;
$$;

REVOKE ALL ON FUNCTION brickvault.capture_draft_backing_withdrawal() FROM PUBLIC;
REVOKE ALL ON FUNCTION brickvault.guard_stock_loss_reductions() FROM PUBLIC;

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
            COALESCE(sum(b.backing_delta) FILTER (WHERE COALESCE(origin.purchase_revision,c.revision)<=NEW.expected_purchase_revision),0)::integer AS prior
        FROM brickvault.stock_loss_offer_reductions chosen
        LEFT JOIN brickvault.offer_backing_ledger b
            ON b.offer_id=chosen.offer_id AND b.placement_id=NEW.position_id
        LEFT JOIN brickvault.offer_backing origin ON b.kind='offer-backing' AND origin.offer_id=b.offer_id AND origin.placement_id=b.placement_id
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

ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check CHECK (kind IN
    ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release',
     'stock-loss','recount','offer-withdrawal','quarantine','quarantine-release','shortage-closure','stock-loss-recovery',
     'part-out','part-out-output','location-label','location-archive','location-capacity','set-inspection','pooled-offer-unapplied'));
ALTER TABLE brickvault.commands ADD CONSTRAINT pooled_offer_unapplied_identity
    CHECK (kind <> 'pooled-offer-unapplied' OR (entity_id=command_id AND revision=1));


-- Values 0..3 are observed physical contents. NULL means no observation; zero is an
-- observed set with neither box nor instructions. Historical rows retain NULL.
ALTER TABLE brickvault.deliveries ADD COLUMN set_contents smallint CHECK (set_contents BETWEEN 0 AND 3);
ALTER TABLE brickvault.placements ADD COLUMN set_contents smallint CHECK (set_contents BETWEEN 0 AND 3);
ALTER TABLE brickvault.stock_part_out_outputs ADD COLUMN set_contents smallint CHECK (set_contents BETWEEN 0 AND 3);
ALTER TABLE brickvault.offers ADD COLUMN set_contents smallint CHECK (set_contents BETWEEN 0 AND 3);

CREATE TABLE brickvault.position_set_contents (
    position_id uuid PRIMARY KEY REFERENCES brickvault.placement_positions(id),
    set_contents smallint NOT NULL CHECK (set_contents BETWEEN 0 AND 3)
);

CREATE TABLE brickvault.set_position_inspections (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision>0),
    source_position_id uuid NOT NULL,
    result_position_id uuid NOT NULL UNIQUE REFERENCES brickvault.placement_positions(id),
    catalog_id uuid NOT NULL REFERENCES brickvault.catalog(id),
    catalog_revision integer NOT NULL CHECK (catalog_revision>0),
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    prior_quantity integer NOT NULL CHECK (prior_quantity BETWEEN quantity AND 1000000),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    set_contents smallint NOT NULL CHECK (set_contents BETWEEN 0 AND 3),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    FOREIGN KEY (purchase_id,source_position_id,catalog_id,catalog_revision)
        REFERENCES brickvault.placement_positions(purchase_id,id,catalog_id,catalog_revision),
    FOREIGN KEY (purchase_id,result_position_id,catalog_id,catalog_revision)
        REFERENCES brickvault.placement_positions(purchase_id,id,catalog_id,catalog_revision),
    UNIQUE (purchase_id,expected_purchase_revision)
);
CREATE INDEX set_inspections_source ON brickvault.set_position_inspections(source_position_id);

-- Keep the originally posted loss position distinct from the position occupied after recovery.
ALTER TABLE brickvault.stock_loss_recoveries ADD COLUMN set_contents smallint CHECK (set_contents BETWEEN 0 AND 3),
    ADD COLUMN result_position_id uuid REFERENCES brickvault.placement_positions(id) DEFERRABLE INITIALLY DEFERRED;
UPDATE brickvault.stock_loss_recoveries SET result_position_id=position_id;
ALTER TABLE brickvault.stock_loss_recoveries ALTER COLUMN result_position_id SET NOT NULL;
CREATE INDEX stock_loss_recoveries_result_position ON brickvault.stock_loss_recoveries(result_position_id);

CREATE OR REPLACE VIEW brickvault.current_placement_stock AS
SELECT p.*,o.delivery_id,
    (p.introduced_quantity
        - COALESCE((SELECT sum(t.quantity) FROM brickvault.placement_transfers t WHERE t.source_position_id=p.id),0)
        - COALESCE((SELECT sum(l.lost_quantity) FROM brickvault.stock_loss_postings l WHERE l.position_id=p.id),0)
        + COALESCE((SELECT sum(r.saleable_quantity+r.quarantine_quantity)
                    FROM brickvault.stock_loss_recoveries r WHERE r.position_id=p.id AND r.result_position_id=p.id),0)
        - COALESCE((SELECT sum(x.quantity) FROM brickvault.stock_part_outs x WHERE x.source_position_id=p.id),0)
        - COALESCE((SELECT sum(s.quantity) FROM brickvault.set_position_inspections s
                    WHERE s.source_position_id=p.id AND s.result_position_id<>s.source_position_id),0))::integer AS quantity
FROM brickvault.placement_positions p JOIN brickvault.placements o ON o.id=p.origin_placement_id;

CREATE OR REPLACE VIEW brickvault.stock_position_physical_deltas AS
SELECT t.source_position_id AS position_id,c.revision,-t.quantity AS delta
FROM brickvault.placement_transfers t JOIN brickvault.commands c ON c.command_id=t.command_id
UNION ALL
SELECT l.position_id,c.revision,-l.lost_quantity
FROM brickvault.stock_loss_postings l JOIN brickvault.commands c ON c.command_id=l.command_id
UNION ALL
SELECT r.position_id,c.revision,r.saleable_quantity+r.quarantine_quantity
FROM brickvault.stock_loss_recoveries r JOIN brickvault.commands c ON c.command_id=r.command_id WHERE r.result_position_id=r.position_id
UNION ALL
SELECT p.source_position_id,c.revision,-p.quantity
FROM brickvault.stock_part_outs p JOIN brickvault.commands c ON c.command_id=p.command_id
UNION ALL
SELECT s.source_position_id,c.revision,-s.quantity FROM brickvault.set_position_inspections s
JOIN brickvault.commands c ON c.command_id=s.command_id WHERE s.result_position_id<>s.source_position_id;


-- A generated part-out delivery/placement carries its own observed output contents. A
-- receiving location never supplies new identifying facts for a previously observed delivery.
CREATE FUNCTION brickvault.guard_set_receipt_contents() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE recorded smallint;
BEGIN
    IF TG_TABLE_NAME='deliveries' THEN
        SELECT set_contents INTO recorded FROM brickvault.stock_part_out_outputs
        WHERE delivery_id=NEW.id AND child_command_id=NEW.command_id;
    ELSIF TG_TABLE_NAME='placements' THEN
        SELECT set_contents INTO recorded FROM brickvault.deliveries WHERE id=NEW.delivery_id;
    END IF;
    IF recorded IS NOT NULL THEN
        IF NEW.set_contents IS NOT NULL AND NEW.set_contents<>recorded THEN
            RAISE check_violation USING CONSTRAINT='set_contents_mismatch';
        END IF;
        NEW.set_contents := recorded;
    END IF;
    IF NEW.set_contents IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM brickvault.catalog WHERE id=NEW.catalog_id AND item_type='set'
    ) THEN RAISE check_violation USING CONSTRAINT='set_contents_not_set'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER verify_set_contents BEFORE INSERT ON brickvault.deliveries
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_set_receipt_contents();
CREATE TRIGGER verify_set_contents BEFORE INSERT ON brickvault.placements
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_set_receipt_contents();
CREATE TRIGGER verify_set_contents BEFORE INSERT ON brickvault.stock_part_out_outputs
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_set_receipt_contents();

CREATE FUNCTION brickvault.inherit_position_set_contents() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE recorded smallint;
BEGIN
    IF TG_TABLE_NAME='placements' THEN
        recorded := NEW.set_contents;
    ELSE
        SELECT set_contents INTO recorded FROM brickvault.position_set_contents WHERE position_id=NEW.source_position_id;
    END IF;
    IF recorded IS NOT NULL THEN
        INSERT INTO brickvault.position_set_contents(position_id,set_contents) VALUES(NEW.id,recorded);
    END IF;
    RETURN NULL;
END;
$$;
-- Seed position and transfer guards create the physical position before these evidence rows.
CREATE TRIGGER store_set_contents AFTER INSERT ON brickvault.placements
    FOR EACH ROW EXECUTE FUNCTION brickvault.inherit_position_set_contents();
CREATE TRIGGER store_set_contents AFTER INSERT ON brickvault.placement_transfers
    FOR EACH ROW EXECUTE FUNCTION brickvault.inherit_position_set_contents();
REVOKE ALL ON FUNCTION brickvault.inherit_position_set_contents() FROM PUBLIC;

CREATE FUNCTION brickvault.guard_set_offer_contents() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE kind text; promised smallint; actual smallint;
BEGIN
    SELECT c.item_type,o.set_contents INTO kind,promised
    FROM brickvault.offers o JOIN brickvault.catalog c ON c.id=o.catalog_id WHERE o.id=NEW.offer_id;
    IF kind='set' THEN
        SELECT set_contents INTO actual FROM brickvault.position_set_contents WHERE position_id=NEW.placement_id;
        IF actual IS NULL OR promised IS NULL THEN RAISE check_violation USING CONSTRAINT='set_contents_unverified'; END IF;
        IF promised<>actual THEN RAISE check_violation USING CONSTRAINT='set_contents_mismatch'; END IF;
    ELSIF promised IS NOT NULL THEN
        RAISE check_violation USING CONSTRAINT='set_contents_not_set';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER verify_offer_contents BEFORE INSERT ON brickvault.offer_backing
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_set_offer_contents();

CREATE FUNCTION brickvault.inspect_position_set_contents() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE stock brickvault.current_placement_stock%ROWTYPE; protected integer;
BEGIN
    UPDATE brickvault.purchases SET revision=revision+1 WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='set_inspection_revision'; END IF;
    SELECT * INTO stock FROM brickvault.current_placement_stock WHERE id=NEW.source_position_id AND purchase_id=NEW.purchase_id
        AND location_id=NEW.location_id AND catalog_id=NEW.catalog_id AND catalog_revision=NEW.catalog_revision;
    IF NOT FOUND OR NOT EXISTS (SELECT 1 FROM brickvault.catalog WHERE id=NEW.catalog_id
        AND revision=NEW.catalog_revision AND item_type='set' FOR SHARE) THEN
        RAISE check_violation USING CONSTRAINT='set_inspection_identity';
    END IF;
    IF EXISTS (SELECT 1 FROM brickvault.position_set_contents WHERE position_id=stock.id) THEN
        RAISE check_violation USING CONSTRAINT='set_contents_already_recorded';
    END IF;
    IF EXISTS (SELECT 1 FROM brickvault.stock_shortage_cases WHERE position_id=stock.id AND resolved_by_command_id IS NULL) THEN
        RAISE check_violation USING CONSTRAINT='stock_shortage_open';
    END IF;
    NEW.location_code := brickvault.confirm_location_label(NEW.location_id,NEW.location_code,false,'set_inspection_bin');
    PERFORM brickvault.lock_location_capacity_fences(ARRAY[NEW.location_id]);
    NEW.prior_quantity := stock.quantity;
    IF NEW.quantity>stock.quantity THEN RAISE check_violation USING CONSTRAINT='set_inspection_quantity'; END IF;
    -- Inspecting the entire position leaves every existing commitment in place. A later
    -- recovery with different or unrecorded contents receives a separate physical position.
    IF NEW.quantity<stock.quantity THEN
        SELECT COALESCE((SELECT sum(quantity) FROM brickvault.current_offer_backing WHERE placement_id=stock.id),0)
            +COALESCE((SELECT sum(remaining_quantity) FROM brickvault.current_build_holds WHERE position_id=stock.id),0)
            +COALESCE((SELECT quantity FROM brickvault.current_position_quarantine WHERE position_id=stock.id),0) INTO protected;
        IF NEW.quantity>stock.quantity-protected THEN RAISE check_violation USING CONSTRAINT='set_inspection_committed'; END IF;
        NEW.result_position_id := NEW.id;
        INSERT INTO brickvault.placement_positions(id,origin_placement_id,purchase_id,catalog_id,catalog_revision,
            location_id,location_code,introduced_quantity)
        VALUES (NEW.id,stock.origin_placement_id,stock.purchase_id,stock.catalog_id,stock.catalog_revision,
            stock.location_id,NEW.location_code,NEW.quantity);
    ELSE
        NEW.result_position_id := stock.id;
    END IF;
    INSERT INTO brickvault.position_set_contents(position_id,set_contents) VALUES(NEW.result_position_id,NEW.set_contents);
    RETURN NEW;
END;
$$;
CREATE TRIGGER inspect_position_set_contents BEFORE INSERT ON brickvault.set_position_inspections
    FOR EACH ROW EXECUTE FUNCTION brickvault.inspect_position_set_contents();
REVOKE ALL ON FUNCTION brickvault.inspect_position_set_contents() FROM PUBLIC;

CREATE FUNCTION brickvault.require_set_inspection() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM brickvault.set_position_inspections i JOIN brickvault.commands c USING(command_id)
        JOIN brickvault.audit a USING(command_id) WHERE i.command_id=NEW.command_id AND c.entity_id=i.id
        AND c.kind='set-inspection' AND c.revision=i.expected_purchase_revision+1) THEN
        RAISE check_violation USING CONSTRAINT='set_inspection_receipt_required';
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER require_set_inspection AFTER INSERT ON brickvault.set_position_inspections
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_set_inspection();
CREATE CONSTRAINT TRIGGER require_set_inspection_command AFTER INSERT ON brickvault.commands
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.kind='set-inspection') EXECUTE FUNCTION brickvault.require_set_inspection();

-- Recovered pieces occupy the original layer and bin. A changed or unrecorded accessory
-- configuration cannot acquire the source position's promise, even on a retained client.
CREATE FUNCTION brickvault.choose_set_recovery_position() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE kind text;
BEGIN
    SELECT c.item_type INTO kind FROM brickvault.placement_positions p
    JOIN brickvault.catalog c ON c.id=p.catalog_id WHERE p.id=NEW.position_id;
    IF NEW.set_contents IS NOT NULL AND kind IS DISTINCT FROM 'set' THEN
        RAISE check_violation USING CONSTRAINT='set_contents_not_set';
    END IF;
    NEW.result_position_id := NEW.position_id;
    RETURN NEW;
END;
$$;
CREATE TRIGGER verify_set_recovery BEFORE INSERT ON brickvault.stock_loss_recoveries
    FOR EACH ROW EXECUTE FUNCTION brickvault.choose_set_recovery_position();

CREATE OR REPLACE FUNCTION brickvault.require_stock_loss_recovery_receipt() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    credited_position uuid := NEW.position_id;
    line_count integer;
    updated_lines integer;
    sold integer;
    quarantined integer;
    basis integer;
    delivery brickvault.deliveries%ROWTYPE;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.commands c JOIN brickvault.audit a USING(command_id)
        WHERE c.command_id=NEW.command_id AND c.kind='stock-loss-recovery' AND c.entity_id=NEW.id
            AND c.revision-1=NEW.expected_purchase_revision
    ) THEN RAISE check_violation USING CONSTRAINT='stock_loss_recovery_receipt_required'; END IF;

    SELECT d.* INTO STRICT delivery FROM brickvault.deliveries d
    JOIN brickvault.stock_loss_postings l ON l.delivery_id=d.id WHERE l.id=NEW.loss_id;
    SELECT count(*)::integer,COALESCE(sum(saleable_quantity),0)::integer,COALESCE(sum(quarantine_quantity),0)::integer
    INTO line_count,sold,quarantined FROM brickvault.stock_loss_recovery_allocations WHERE recovery_id=NEW.id;
    IF line_count=0 OR sold::bigint+quarantined>delivery.quantity THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_recovery_amount';
    END IF;

    WITH prior AS (
        SELECT COALESCE(a.quarantine_id,'00000000-0000-0000-0000-000000000000') AS key,
            sum(a.saleable_quantity+a.quarantine_quantity)::integer AS quantity
        FROM brickvault.stock_loss_recoveries r JOIN brickvault.stock_loss_recovery_allocations a ON a.recovery_id=r.id
        WHERE r.loss_id=NEW.loss_id AND r.id<>NEW.id AND r.saleable_quantity+r.quarantine_quantity>0
        GROUP BY 1
    ), sources AS (
        SELECT COALESCE(s.quarantine_id,'00000000-0000-0000-0000-000000000000') AS key,
            s.original_quantity,s.source_offset
        FROM brickvault.stock_loss_recovery_sources(NEW.loss_id) s
    ), valid AS (
        SELECT a.quarantine_id,brickvault.slice_basis_slots(original.basis_slots,s.source_offset+COALESCE(p.quantity,0),
            a.saleable_quantity+a.quarantine_quantity) AS slots
        FROM brickvault.stock_loss_recovery_allocations a JOIN sources s
            ON s.key=COALESCE(a.quarantine_id,'00000000-0000-0000-0000-000000000000')
        LEFT JOIN prior p ON p.key=s.key
        JOIN brickvault.original_stock_loss_basis original ON original.loss_id=NEW.loss_id
        WHERE a.recovery_id=NEW.id
            AND a.saleable_quantity+a.quarantine_quantity<=s.original_quantity-COALESCE(p.quantity,0)
    )
    UPDATE brickvault.stock_loss_recovery_allocations a
    SET basis_slots=v.slots,restored_item_cost_minor=brickvault.basis_slot_amount(v.slots,delivery.quantity,delivery.item_cost_minor)
    FROM valid v WHERE a.recovery_id=NEW.id
        AND COALESCE(a.quarantine_id,'00000000-0000-0000-0000-000000000000')
            = COALESCE(v.quarantine_id,'00000000-0000-0000-0000-000000000000')
        AND NOT isempty(v.slots);
    GET DIAGNOSTICS updated_lines = ROW_COUNT;
    IF updated_lines<>line_count THEN RAISE check_violation USING CONSTRAINT='stock_loss_recovery_amount'; END IF;

    SELECT sum(restored_item_cost_minor)::integer INTO basis
    FROM brickvault.stock_loss_recovery_allocations WHERE recovery_id=NEW.id;
    IF (SELECT set_contents FROM brickvault.position_set_contents WHERE position_id=NEW.position_id)
            IS DISTINCT FROM NEW.set_contents THEN
        credited_position := NEW.id;
        INSERT INTO brickvault.placement_positions(id,origin_placement_id,purchase_id,catalog_id,catalog_revision,
            location_id,location_code,introduced_quantity)
        SELECT credited_position,p.origin_placement_id,p.purchase_id,p.catalog_id,p.catalog_revision,p.location_id,
            NEW.location_code,sold+quarantined FROM brickvault.placement_positions p WHERE p.id=NEW.position_id;
        IF NEW.set_contents IS NOT NULL THEN
            INSERT INTO brickvault.position_set_contents(position_id,set_contents) VALUES(credited_position,NEW.set_contents);
        END IF;
    END IF;
    UPDATE brickvault.stock_loss_recoveries
    SET result_position_id=credited_position,saleable_quantity=sold,quarantine_quantity=quarantined,restored_item_cost_minor=basis WHERE id=NEW.id;

    IF quarantined>0 THEN
        INSERT INTO brickvault.stock_quarantines(id,command_id,purchase_id,position_id,location_id,location_code,
            catalog_id,catalog_revision,recorded_catalog,expected_purchase_revision,quantity,reason)
        VALUES (NEW.id,NEW.command_id,NEW.purchase_id,credited_position,NEW.location_id,NEW.location_code,
            NEW.catalog_id,NEW.catalog_revision,NEW.recorded_catalog,NEW.expected_purchase_revision,quarantined,NEW.reason);
    END IF;
    RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.guard_stock_quarantine() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    stock brickvault.current_placement_stock%ROWTYPE;
    catalog brickvault.catalog%ROWTYPE;
    release_receipt boolean := TG_TABLE_NAME='stock_quarantine_releases';
    available integer;
BEGIN
    IF NOT release_receipt AND EXISTS (
        SELECT 1 FROM brickvault.stock_part_out_outputs o JOIN brickvault.stock_part_outs p ON p.id=o.part_out_id
        JOIN brickvault.commands c ON c.command_id=o.child_command_id
        WHERE o.position_id=NEW.id AND o.position_id=NEW.position_id AND o.child_command_id=NEW.command_id
            AND c.kind='part-out-output' AND c.entity_id=o.delivery_id AND o.disposition='retained'
            AND p.purchase_id=NEW.purchase_id AND o.location_id=NEW.location_id AND o.location_code=NEW.location_code
            AND o.catalog_id=NEW.catalog_id AND o.catalog_revision=NEW.catalog_revision
            AND p.expected_purchase_revision=NEW.expected_purchase_revision AND o.recorded_catalog=NEW.recorded_catalog
            AND o.quantity=NEW.quantity AND p.reason=NEW.reason
    ) THEN RETURN NEW; END IF;

    -- The signed recovery command already advances the purchase and identifies the restored units.
    IF NOT release_receipt AND EXISTS (
        SELECT 1 FROM brickvault.stock_loss_recoveries r JOIN brickvault.commands c ON c.command_id=r.command_id
        WHERE r.id=NEW.id AND r.command_id=NEW.command_id AND c.kind='stock-loss-recovery' AND c.entity_id=r.id
            AND r.purchase_id=NEW.purchase_id AND r.result_position_id=NEW.position_id AND r.location_id=NEW.location_id
            AND r.location_code=NEW.location_code AND r.catalog_id=NEW.catalog_id AND r.catalog_revision=NEW.catalog_revision
            AND r.expected_purchase_revision=NEW.expected_purchase_revision AND r.recorded_catalog=NEW.recorded_catalog
            AND r.quarantine_quantity=NEW.quantity AND r.reason=NEW.reason
    ) THEN RETURN NEW;
    END IF;

    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='quarantine_revision_conflict'; END IF;

    SELECT * INTO stock FROM brickvault.current_placement_stock
    WHERE id=NEW.position_id AND purchase_id=NEW.purchase_id AND location_id=NEW.location_id AND quantity>0;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='quarantine_position_mismatch'; END IF;
    NEW.location_code := brickvault.confirm_location_label(
        NEW.location_id,NEW.location_code,false,'quarantine_bin_conflict');
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
