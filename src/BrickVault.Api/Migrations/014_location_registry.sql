
ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check CHECK (kind IN
    ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release',
     'stock-loss','recount','offer-withdrawal','quarantine','quarantine-release','shortage-closure','stock-loss-recovery',
     'part-out','part-out-output','location-label','location-archive'));

CREATE TABLE brickvault.location_labels (
    label_key text PRIMARY KEY,
    label varchar(48) NOT NULL CHECK (label ~ '^[A-Za-z0-9][A-Za-z0-9._-]*$' COLLATE "C"),
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    claimed_revision integer NOT NULL CHECK (claimed_revision>0),
    CHECK (label_key=upper(label COLLATE "C"))
);
CREATE INDEX location_labels_by_location ON brickvault.location_labels(location_id,label_key);

-- Only current canonical labels are known to be registered in the prior schema.
-- Older stock receipt spellings and audit IDs do not prove label ownership or earlier canonical changes.
INSERT INTO brickvault.location_labels(label_key,label,location_id,claimed_revision)
SELECT code,code,id,revision FROM brickvault.locations;

CREATE TABLE brickvault.location_registry_events (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    revision integer NOT NULL CHECK (revision>0),
    kind text NOT NULL CHECK (kind IN ('registered','canonical-renamed','location-updated','label-added','archived')),
    label varchar(48),
    previous_code varchar(48),
    canonical_code varchar(48) NOT NULL,
    name varchar(160) NOT NULL,
    reason varchar(200),
    physical_quantity bigint,
    position_count bigint,
    UNIQUE (location_id,revision),
    CHECK (
        (kind='archived' AND label IS NOT NULL AND reason IS NOT NULL AND length(trim(reason))>0
            AND physical_quantity>=0 AND position_count>=0 AND num_nonnulls(physical_quantity,position_count)=2)
        OR (kind<>'archived' AND reason IS NULL AND num_nonnulls(physical_quantity,position_count)=0)
    )
);
CREATE UNIQUE INDEX location_single_archive ON brickvault.location_registry_events(location_id) WHERE kind='archived';
CREATE INDEX location_reference_receipts ON brickvault.commands(entity_id,revision) WHERE kind='location';

CREATE FUNCTION brickvault.claim_location_label(
    bin uuid, spelling text, claimed_at_revision integer, allow_existing_for_bin boolean
) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    key text := upper(spelling COLLATE "C");
BEGIN
    INSERT INTO brickvault.location_labels(label_key,label,location_id,claimed_revision)
    VALUES (key,spelling,bin,claimed_at_revision)
    ON CONFLICT (label_key) DO NOTHING;
    IF NOT FOUND AND (NOT allow_existing_for_bin OR NOT EXISTS (
        SELECT 1 FROM brickvault.location_labels WHERE label_key=key AND location_id=bin
    )) THEN
        RAISE check_violation USING CONSTRAINT='duplicate_location_label';
    END IF;
END;
$$;
REVOKE ALL ON FUNCTION brickvault.claim_location_label(uuid,text,integer,boolean) FROM PUBLIC;

CREATE FUNCTION brickvault.sync_canonical_location_label() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    IF TG_OP='INSERT' THEN
        IF NEW.revision<>1 THEN RAISE check_violation USING CONSTRAINT='location_registry_revision'; END IF;
    ELSIF NEW.id<>OLD.id OR NEW.revision<>OLD.revision+1 THEN
        RAISE check_violation USING CONSTRAINT='location_registry_revision';
    END IF;

    IF TG_OP='INSERT' OR NEW.code<>OLD.code THEN
        PERFORM brickvault.claim_location_label(NEW.id,NEW.code,NEW.revision,true);
    END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER sync_canonical_location_label AFTER INSERT OR UPDATE ON brickvault.locations
    FOR EACH ROW EXECUTE FUNCTION brickvault.sync_canonical_location_label();
REVOKE ALL ON FUNCTION brickvault.sync_canonical_location_label() FROM PUBLIC;

CREATE FUNCTION brickvault.require_canonical_location_receipt() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    receipt uuid;
BEGIN
    IF TG_OP='UPDATE' AND NEW.code=OLD.code AND NEW.name=OLD.name AND EXISTS (
        SELECT 1 FROM brickvault.location_registry_events e
        WHERE e.location_id=NEW.id AND e.revision=NEW.revision AND e.kind IN ('label-added','archived')
          AND e.canonical_code=NEW.code AND e.name=NEW.name
    ) THEN RETURN NULL; END IF;

    SELECT c.command_id INTO receipt
    FROM brickvault.commands c JOIN brickvault.audit a USING(command_id)
    WHERE c.kind='location' AND c.entity_id=NEW.id AND c.revision=NEW.revision;
    IF receipt IS NULL THEN RAISE check_violation USING CONSTRAINT='location_registry_receipt_required'; END IF;
    INSERT INTO brickvault.location_registry_events(id,command_id,location_id,revision,kind,label,previous_code,canonical_code,name)
    VALUES (receipt,receipt,NEW.id,NEW.revision,
        CASE WHEN TG_OP='INSERT' THEN 'registered' WHEN NEW.code<>OLD.code THEN 'canonical-renamed' ELSE 'location-updated' END,
        CASE WHEN TG_OP='INSERT' OR NEW.code<>OLD.code THEN NEW.code END,
        CASE WHEN TG_OP='UPDATE' AND NEW.code<>OLD.code THEN OLD.code END,NEW.code,NEW.name);
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER require_canonical_location_receipt AFTER INSERT OR UPDATE ON brickvault.locations
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_canonical_location_receipt();
REVOKE ALL ON FUNCTION brickvault.require_canonical_location_receipt() FROM PUBLIC;

CREATE FUNCTION brickvault.confirm_location_label(bin uuid, spelling text, require_active boolean, mismatch text)
RETURNS text LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE
    canonical text;
    registered text;
    key text := upper(spelling COLLATE "C");
BEGIN
    -- Physical writes retain this lock until commit. Archive takes the conflicting lock
    -- before reading the occupancy it is about to record.
    SELECT code INTO canonical FROM brickvault.locations WHERE id=bin FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT=mismatch; END IF;
    SELECT CASE WHEN l.label_key=canonical THEN canonical ELSE l.label END
        INTO registered FROM brickvault.location_labels l WHERE l.label_key=key AND l.location_id=bin;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT=mismatch; END IF;
    IF require_active AND EXISTS (
        SELECT 1 FROM brickvault.location_registry_events WHERE location_id=bin AND kind='archived'
    ) THEN RAISE check_violation USING CONSTRAINT='location_destination_archived'; END IF;
    RETURN registered;
END;
$$;

CREATE FUNCTION brickvault.location_physical_stock(bin uuid) RETURNS TABLE(physical_quantity bigint,position_count bigint)
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
    SELECT COALESCE(sum(s.quantity),0),count(*) FROM brickvault.current_placement_stock s
    WHERE s.location_id=bin AND s.quantity>0;
$$;

CREATE FUNCTION brickvault.capture_location_registry_event() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    bin brickvault.locations%ROWTYPE;
    stock record;
BEGIN
    SELECT * INTO bin FROM brickvault.locations WHERE id=NEW.location_id FOR UPDATE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='unknown_location'; END IF;
    IF NEW.revision<>bin.revision+1 THEN RAISE check_violation USING CONSTRAINT='location_registry_revision'; END IF;

    IF NEW.kind='label-added' THEN
        PERFORM brickvault.claim_location_label(bin.id,NEW.label,NEW.revision,false);
    ELSE
        IF EXISTS (SELECT 1 FROM brickvault.location_registry_events WHERE location_id=bin.id AND kind='archived') THEN
            RAISE check_violation USING CONSTRAINT='location_already_archived';
        END IF;
        NEW.label := brickvault.confirm_location_label(bin.id,NEW.label,false,'location_label_mismatch');
        SELECT * INTO stock FROM brickvault.location_physical_stock(bin.id);
        IF NEW.physical_quantity IS DISTINCT FROM stock.physical_quantity
            OR NEW.position_count IS DISTINCT FROM stock.position_count THEN
            RAISE check_violation USING CONSTRAINT='location_archive_stock_changed';
        END IF;
    END IF;

    NEW.canonical_code := bin.code;
    NEW.name := bin.name;
    UPDATE brickvault.locations SET revision=NEW.revision WHERE id=bin.id;
    RETURN NEW;
END;
$$;
CREATE TRIGGER capture_location_registry_event BEFORE INSERT ON brickvault.location_registry_events
    FOR EACH ROW WHEN (NEW.kind IN ('label-added','archived'))
    EXECUTE FUNCTION brickvault.capture_location_registry_event();
REVOKE ALL ON FUNCTION brickvault.capture_location_registry_event() FROM PUBLIC;

CREATE FUNCTION brickvault.require_location_registry_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.commands c JOIN brickvault.audit a USING(command_id)
        WHERE c.command_id=NEW.command_id AND c.entity_id=NEW.id AND c.revision=NEW.revision
            AND c.kind=CASE WHEN NEW.kind='archived' THEN 'location-archive' ELSE 'location-label' END
    ) THEN RAISE check_violation USING CONSTRAINT='location_registry_receipt_required'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER require_location_registry_receipt AFTER INSERT ON brickvault.location_registry_events
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.kind IN ('label-added','archived'))
    EXECUTE FUNCTION brickvault.require_location_registry_receipt();

CREATE FUNCTION brickvault.require_location_registry_command() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.location_registry_events e
        WHERE e.command_id=NEW.command_id AND e.id=NEW.entity_id AND e.revision=NEW.revision
            AND e.kind=CASE WHEN NEW.kind='location-archive' THEN 'archived' ELSE 'label-added' END
    ) THEN RAISE check_violation USING CONSTRAINT='location_registry_receipt_required'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER require_location_registry_command AFTER INSERT ON brickvault.commands
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.kind IN ('location-label','location-archive'))
    EXECUTE FUNCTION brickvault.require_location_registry_command();

-- Existing stock rules keep their validation and rejection codes while removing canonical-only confirmation.

CREATE OR REPLACE FUNCTION brickvault.capture_stock_count() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- The insert owns the revision increment for every writer, including a recount racing a receipt.
    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN
        RAISE check_violation USING CONSTRAINT='count_revision';
    END IF;

    SELECT s.quantity INTO NEW.ledger_quantity
    FROM brickvault.current_placement_stock s
    WHERE s.id=NEW.position_id AND s.purchase_id=NEW.purchase_id AND s.location_id=NEW.location_id AND s.quantity>0;
    IF NOT FOUND OR EXISTS (SELECT 1 FROM brickvault.commands WHERE command_id=NEW.command_id) THEN
        RAISE check_violation USING CONSTRAINT='count_position';
    END IF;
    NEW.location_code := brickvault.confirm_location_label(
        NEW.location_id,NEW.location_code,false,'count_bin');

    SELECT COALESCE(sum(quantity),0)::integer INTO NEW.offer_backed_quantity
    FROM brickvault.current_offer_backing WHERE placement_id=NEW.position_id;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.guard_build_hold() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    stock brickvault.current_placement_stock%ROWTYPE;
    reserved integer;
    backed integer;
BEGIN
    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='hold_revision'; END IF;

    SELECT * INTO stock FROM brickvault.current_placement_stock
    WHERE id=NEW.position_id AND purchase_id=NEW.purchase_id AND location_id=NEW.location_id AND quantity>0;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='hold_position'; END IF;
    IF EXISTS (SELECT 1 FROM brickvault.stock_shortage_cases
               WHERE position_id=NEW.position_id AND resolved_by_command_id IS NULL) THEN
        RAISE check_violation USING CONSTRAINT='stock_shortage_open';
    END IF;
    NEW.location_code := brickvault.confirm_location_label(
        NEW.location_id,NEW.location_code,false,'hold_bin');
    PERFORM 1 FROM brickvault.catalog
    WHERE id=stock.catalog_id AND revision=stock.catalog_revision AND integration_eligible FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='hold_catalog'; END IF;

    SELECT COALESCE(sum(remaining_quantity),0)::integer INTO reserved
    FROM brickvault.current_build_holds WHERE position_id=NEW.position_id;
    SELECT COALESCE(sum(quantity),0)::integer INTO backed
    FROM brickvault.current_offer_backing WHERE placement_id=NEW.position_id;
    IF NEW.quantity > stock.quantity-reserved-backed THEN
        RAISE check_violation USING CONSTRAINT='hold_quantity';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.capture_draft_backing_withdrawal() RETURNS trigger LANGUAGE plpgsql AS $$
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
            AND r.purchase_id=NEW.purchase_id AND r.position_id=NEW.position_id AND r.location_id=NEW.location_id
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

CREATE OR REPLACE FUNCTION brickvault.capture_stock_shortage_closure() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    opening brickvault.stock_count_observations%ROWTYPE;
    witness brickvault.stock_count_observations%ROWTYPE;
BEGIN
    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='shortage_closure_revision'; END IF;

    SELECT quantity INTO NEW.ledger_quantity FROM brickvault.current_placement_stock
    WHERE id=NEW.position_id AND purchase_id=NEW.purchase_id AND location_id=NEW.location_id AND quantity>0;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='shortage_closure_position_mismatch'; END IF;
    NEW.location_code := brickvault.confirm_location_label(
        NEW.location_id,NEW.location_code,false,'shortage_closure_bin_conflict');

    SELECT n.* INTO opening FROM brickvault.stock_shortage_cases s
    JOIN brickvault.stock_count_observations n ON n.id=s.id
    WHERE s.id=NEW.case_id AND s.position_id=NEW.position_id AND s.resolved_by_command_id IS NULL
      AND n.purchase_id=NEW.purchase_id AND n.location_id=NEW.location_id AND n.counted_quantity<n.ledger_quantity;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='shortage_closure_case_conflict'; END IF;

    SELECT * INTO witness FROM brickvault.stock_count_observations
    WHERE position_id=NEW.position_id ORDER BY expected_purchase_revision DESC LIMIT 1;
    IF witness.id IS DISTINCT FROM NEW.observation_id OR witness.purchase_id IS DISTINCT FROM NEW.purchase_id
        OR witness.location_id IS DISTINCT FROM NEW.location_id OR witness.ledger_quantity IS DISTINCT FROM NEW.ledger_quantity THEN
        RAISE check_violation USING CONSTRAINT='shortage_closure_observation_conflict';
    END IF;
    IF witness.expected_purchase_revision<=opening.expected_purchase_revision OR witness.counted_quantity<>NEW.ledger_quantity THEN
        RAISE check_violation USING CONSTRAINT='shortage_closure_count_not_exact';
    END IF;

    SELECT COALESCE(sum(remaining_quantity),0)::integer INTO NEW.held_quantity
    FROM brickvault.current_build_holds WHERE position_id=NEW.position_id;
    SELECT COALESCE(sum(quantity),0)::integer INTO NEW.offer_backed_quantity
    FROM brickvault.current_offer_backing WHERE placement_id=NEW.position_id;
    SELECT COALESCE(sum(quantity),0)::integer INTO NEW.quarantined_quantity
    FROM brickvault.current_position_quarantine WHERE position_id=NEW.position_id;
    IF NEW.held_quantity+NEW.offer_backed_quantity+NEW.quarantined_quantity>NEW.ledger_quantity
        OR NEW.offer_backed_quantity<>witness.offer_backed_quantity THEN
        RAISE check_violation USING CONSTRAINT='shortage_closure_commitments_inconsistent';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.capture_stock_loss() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    stock brickvault.current_placement_stock%ROWTYPE;
    witness brickvault.stock_count_observations%ROWTYPE;
    delivery brickvault.deliveries%ROWTYPE;
    active_basis integer;
    had_recovery boolean;
    assigned int4multirange;
    consumed brickvault.delivery_part_out_balances%ROWTYPE;
BEGIN
    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='stock_loss_revision'; END IF;

    SELECT * INTO stock FROM brickvault.current_placement_stock
    WHERE id=NEW.position_id AND purchase_id=NEW.purchase_id AND location_id=NEW.location_id AND quantity>0;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='stock_loss_position_mismatch'; END IF;
    NEW.location_code := brickvault.confirm_location_label(
        NEW.location_id,NEW.location_code,false,'stock_loss_bin_confirmation_mismatch');
    PERFORM 1 FROM brickvault.stock_shortage_cases
    WHERE id=NEW.case_id AND position_id=NEW.position_id AND resolved_by_command_id IS NULL;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='stock_loss_case_conflict'; END IF;

    SELECT * INTO witness FROM brickvault.stock_count_observations
    WHERE position_id=NEW.position_id ORDER BY expected_purchase_revision DESC LIMIT 1;
    IF witness.id IS DISTINCT FROM NEW.observation_id OR witness.ledger_quantity IS DISTINCT FROM stock.quantity
        OR witness.location_id IS DISTINCT FROM NEW.location_id THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_observation_conflict';
    END IF;
    IF witness.counted_quantity>=stock.quantity THEN RAISE check_violation USING CONSTRAINT='stock_loss_no_loss'; END IF;

    NEW.delivery_id := stock.delivery_id;
    NEW.ledger_quantity_before := stock.quantity;
    NEW.lost_quantity := stock.quantity-witness.counted_quantity;
    SELECT COALESCE(sum(remaining_quantity),0)::integer INTO NEW.protected_project_quantity
    FROM brickvault.current_build_holds WHERE position_id=NEW.position_id;
    IF NEW.protected_project_quantity>witness.counted_quantity THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_protected_shortfall';
    END IF;
    SELECT COALESCE(sum(quantity),0)::integer INTO NEW.offer_backed_quantity_before
    FROM brickvault.current_offer_backing WHERE placement_id=NEW.position_id;

    SELECT * INTO STRICT delivery FROM brickvault.deliveries WHERE id=NEW.delivery_id;
    SELECT active_lost_quantity,active_allocated_item_cost_minor,recovered_quantity>0
    INTO STRICT NEW.prior_delivery_loss_quantity,active_basis,had_recovery
    FROM brickvault.delivery_loss_balances WHERE delivery_id=NEW.delivery_id;
    SELECT * INTO STRICT consumed FROM brickvault.delivery_part_out_balances WHERE delivery_id=NEW.delivery_id;
    assigned := brickvault.slice_basis_slots(brickvault.open_stock_basis_slots(NEW.delivery_id),0,NEW.lost_quantity);
    IF consumed.transformed_out_quantity>0 THEN
        INSERT INTO brickvault.stock_loss_part_out_context(loss_id,prior_transformed_quantity,prior_transformed_item_cost_minor)
        VALUES (NEW.id,consumed.transformed_out_quantity,consumed.transformed_out_item_cost_minor);
    END IF;
    IF (SELECT COALESCE(sum(upper(span)-lower(span)),0) FROM unnest(assigned) AS spans(span))<>NEW.lost_quantity THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_basis_unavailable';
    END IF;
    INSERT INTO brickvault.stock_loss_basis_assignments(loss_id,basis_slots,had_prior_recovery)
    VALUES (NEW.id,assigned,had_recovery);
    NEW.allocated_item_cost_minor := brickvault.basis_slot_amount(assigned,delivery.quantity,delivery.item_cost_minor);
    NEW.remaining_delivery_item_cost_minor := delivery.item_cost_minor-active_basis-consumed.transformed_out_item_cost_minor-NEW.allocated_item_cost_minor;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.capture_stock_loss_recovery() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    catalog brickvault.catalog%ROWTYPE;
BEGIN
    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='stock_loss_recovery_revision'; END IF;
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.stock_loss_postings l JOIN brickvault.stock_shortage_cases c ON c.id=l.case_id
        WHERE l.id=NEW.loss_id AND l.position_id=NEW.position_id AND l.purchase_id=NEW.purchase_id
            AND l.location_id=NEW.location_id AND c.resolved_by_command_id=l.command_id
    ) THEN RAISE check_violation USING CONSTRAINT='stock_loss_recovery_scope'; END IF;
    NEW.location_code := brickvault.confirm_location_label(
        NEW.location_id,NEW.location_code,false,'stock_loss_recovery_bin');
    SELECT c.* INTO catalog FROM brickvault.catalog c
    JOIN brickvault.placement_positions p ON p.id=NEW.position_id AND p.catalog_id=c.id
    WHERE c.id=NEW.catalog_id AND c.revision=NEW.catalog_revision FOR SHARE OF c;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='stock_loss_recovery_catalog'; END IF;
    NEW.recorded_catalog := jsonb_build_object(
        'id',catalog.id,'revision',catalog.revision,'itemType',catalog.item_type,'itemNumber',catalog.item_number,
        'variant',catalog.variant,'name',catalog.name,'colorId',catalog.color_id,'colorState',catalog.color_state,
        'condition',catalog.condition,'completeness',catalog.completeness,'source',catalog.source,
        'license',catalog.license,'mapping',catalog.mapping,'integrationEligible',catalog.integration_eligible);
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.capture_stock_part_out() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    stock brickvault.current_placement_stock%ROWTYPE;
    catalog brickvault.catalog%ROWTYPE;
    delivery brickvault.deliveries%ROWTYPE;
    committed integer;
BEGIN
    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='part_out_revision'; END IF;
    SELECT * INTO stock FROM brickvault.current_placement_stock
    WHERE id=NEW.source_position_id AND purchase_id=NEW.purchase_id AND location_id=NEW.source_location_id;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='part_out_scope'; END IF;
    NEW.source_location_code := brickvault.confirm_location_label(
        NEW.source_location_id,NEW.source_location_code,false,'part_out_bin');
    SELECT * INTO catalog FROM brickvault.catalog
    WHERE id=NEW.source_catalog_id AND id=stock.catalog_id AND revision=NEW.source_catalog_revision FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='part_out_catalog'; END IF;
    IF EXISTS (SELECT 1 FROM brickvault.stock_shortage_cases
        WHERE position_id=stock.id AND resolved_by_command_id IS NULL) THEN
        RAISE check_violation USING CONSTRAINT='stock_shortage_open';
    END IF;
    SELECT COALESCE((SELECT sum(quantity) FROM brickvault.current_offer_backing WHERE placement_id=stock.id),0)
        + COALESCE((SELECT sum(remaining_quantity) FROM brickvault.current_build_holds WHERE position_id=stock.id),0)
        + COALESCE((SELECT quantity FROM brickvault.current_position_quarantine WHERE position_id=stock.id),0) INTO committed;
    IF NEW.quantity>stock.quantity-committed THEN RAISE check_violation USING CONSTRAINT='part_out_amount'; END IF;

    SELECT * INTO STRICT delivery FROM brickvault.deliveries WHERE id=stock.delivery_id;
    NEW.source_delivery_id := delivery.id;
    NEW.recorded_source_catalog := brickvault.part_out_catalog_snapshot(catalog);
    NEW.basis_slots := brickvault.slice_basis_slots(brickvault.open_stock_basis_slots(delivery.id),0,NEW.quantity);
    IF (SELECT COALESCE(sum(upper(span)-lower(span)),0) FROM unnest(NEW.basis_slots) AS spans(span))<>NEW.quantity THEN
        RAISE check_violation USING CONSTRAINT='part_out_amount';
    END IF;
    NEW.source_item_cost_minor := brickvault.basis_slot_amount(NEW.basis_slots,delivery.quantity,delivery.item_cost_minor);
    IF (NEW.source_item_cost_minor IS NULL)<>(NEW.residual_item_cost_minor IS NULL)
        OR NEW.residual_item_cost_minor>NEW.source_item_cost_minor THEN
        RAISE check_violation USING CONSTRAINT='part_out_cost_mismatch';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.guard_stock_part_out_line() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    catalog brickvault.catalog%ROWTYPE;
BEGIN
    IF EXISTS (
        SELECT 1 FROM brickvault.stock_part_outs p JOIN brickvault.commands c ON c.command_id=p.command_id
        WHERE p.id=NEW.part_out_id
    ) THEN RAISE check_violation USING CONSTRAINT='part_out_amount'; END IF;
    IF TG_TABLE_NAME='stock_part_out_outputs' THEN
        SELECT * INTO catalog FROM brickvault.catalog
        WHERE id=NEW.catalog_id AND revision=NEW.catalog_revision
            AND (NEW.disposition='retained' OR integration_eligible) FOR SHARE;
        IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='part_out_catalog'; END IF;
        NEW.location_code := brickvault.confirm_location_label(
            NEW.location_id,NEW.location_code,true,'part_out_bin');
        NEW.recorded_catalog := brickvault.part_out_catalog_snapshot(catalog);
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.guard_placement() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    delivery brickvault.deliveries%ROWTYPE;
BEGIN
    UPDATE brickvault.purchases SET revision = revision WHERE id = NEW.purchase_id;
    SELECT * INTO delivery FROM brickvault.deliveries
    WHERE id = NEW.delivery_id AND purchase_id = NEW.purchase_id;
    IF NOT FOUND THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_delivery';
    END IF;
    IF NEW.quantity + COALESCE((SELECT sum(quantity) FROM brickvault.placements
                               WHERE delivery_id = NEW.delivery_id), 0) > delivery.quantity THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_quantity';
    END IF;

    SELECT revision INTO NEW.catalog_revision FROM brickvault.catalog
    WHERE id=NEW.catalog_id AND (integration_eligible OR EXISTS (
        SELECT 1 FROM brickvault.stock_part_out_outputs o
        JOIN brickvault.stock_part_outs p ON p.id=o.part_out_id
        JOIN brickvault.commands c ON c.command_id=o.child_command_id
        WHERE o.disposition='retained' AND o.position_id=NEW.id AND o.delivery_id=delivery.id
            AND o.child_command_id=NEW.command_id AND c.kind='part-out-output' AND c.entity_id=delivery.id
            AND p.purchase_id=NEW.purchase_id AND o.catalog_id=NEW.catalog_id
            AND o.catalog_revision=NEW.confirmed_catalog_revision AND o.catalog_revision=delivery.catalog_revision
            AND o.quantity=NEW.quantity AND o.location_id=NEW.location_id AND o.location_code=NEW.location_code
    )) FOR SHARE;
    IF NOT FOUND OR (delivery.catalog_id IS NOT NULL AND delivery.catalog_id <> NEW.catalog_id) THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_catalog';
    END IF;
    -- A mixed receipt has no recorded catalog version to stand in for explicit operator confirmation.
    IF NEW.confirmed_catalog_revision IS DISTINCT FROM NEW.catalog_revision
        AND (NEW.confirmed_catalog_revision IS NOT NULL OR delivery.catalog_id IS NULL
            OR delivery.catalog_revision <> NEW.catalog_revision) THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_catalog_reconfirmation';
    END IF;
    NEW.location_code := brickvault.confirm_location_label(
        NEW.location_id,NEW.location_code,true,'receiving_bin');
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.guard_placement_transfer() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    source brickvault.current_placement_stock%ROWTYPE;
BEGIN
    UPDATE brickvault.purchases SET revision=revision WHERE id=NEW.purchase_id;
    SELECT * INTO source FROM brickvault.current_placement_stock
    WHERE id=NEW.source_position_id AND purchase_id=NEW.purchase_id AND location_id=NEW.source_location_id;
    IF NOT FOUND OR NEW.source_location_id=NEW.destination_location_id THEN
        RAISE check_violation USING CONSTRAINT='transfer_source';
    END IF;
    IF NEW.quantity > source.quantity THEN
        RAISE check_violation USING CONSTRAINT='transfer_quantity';
    END IF;
    IF EXISTS (SELECT 1 FROM brickvault.commands WHERE command_id=NEW.command_id) THEN
        RAISE check_violation USING CONSTRAINT='transfer_source';
    END IF;

    -- Take both bin locks in UUID order before resolving either label.
    PERFORM id FROM brickvault.locations
    WHERE id IN (NEW.source_location_id,NEW.destination_location_id) ORDER BY id FOR SHARE;
    NEW.source_location_code := brickvault.confirm_location_label(
        NEW.source_location_id,NEW.source_location_code,false,'transfer_bin');
    NEW.destination_location_code := brickvault.confirm_location_label(
        NEW.destination_location_id,NEW.destination_location_code,true,'transfer_bin');
    INSERT INTO brickvault.placement_positions
        (id,origin_placement_id,purchase_id,catalog_id,catalog_revision,location_id,location_code,introduced_quantity)
    VALUES (NEW.id,source.origin_placement_id,source.purchase_id,source.catalog_id,source.catalog_revision,
        NEW.destination_location_id,NEW.destination_location_code,NEW.quantity);
    RETURN NEW;
END;
$$;
