
ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check CHECK (kind IN
    ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release',
     'stock-loss','recount','offer-withdrawal','quarantine','quarantine-release','shortage-closure','stock-loss-recovery',
     'part-out','part-out-output','location-label','location-archive','location-capacity'));

CREATE TABLE brickvault.location_capacity_fences (
    location_id uuid PRIMARY KEY REFERENCES brickvault.locations(id)
);
INSERT INTO brickvault.location_capacity_fences(location_id) SELECT id FROM brickvault.locations;

CREATE FUNCTION brickvault.seed_location_capacity_fence() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    INSERT INTO brickvault.location_capacity_fences(location_id) VALUES (NEW.id);
    RETURN NULL;
END;
$$;
CREATE TRIGGER seed_location_capacity_fence AFTER INSERT ON brickvault.locations
    FOR EACH ROW EXECUTE FUNCTION brickvault.seed_location_capacity_fence();
REVOKE ALL ON FUNCTION brickvault.seed_location_capacity_fence() FROM PUBLIC;

CREATE TABLE brickvault.location_capacity_events (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    revision integer NOT NULL CHECK (revision>0),
    piece_limit bigint CHECK (piece_limit BETWEEN 1 AND 9007199254740991),
    previous_piece_limit bigint CHECK (previous_piece_limit BETWEEN 1 AND 9007199254740991),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    physical_quantity bigint NOT NULL CHECK (physical_quantity>=0),
    position_count bigint NOT NULL CHECK (position_count>=0 AND position_count<=physical_quantity),
    UNIQUE (location_id,revision)
);

CREATE FUNCTION brickvault.lock_location_capacity_fences(locations uuid[]) RETURNS void LANGUAGE plpgsql
    SET search_path=pg_catalog AS $$
DECLARE
    bin uuid;
BEGIN
    FOR bin IN SELECT DISTINCT target FROM unnest(locations) AS targets(target) ORDER BY target LOOP
        -- A new tuple version makes an older repeatable-read stock snapshot fail safely after a concurrent writer.
        -- The UUID value itself is immutable and is the only backed-up field.
        UPDATE brickvault.location_capacity_fences SET location_id=location_id WHERE location_id=bin;
        IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='location_capacity_fence_missing'; END IF;
    END LOOP;
END;
$$;
REVOKE ALL ON FUNCTION brickvault.lock_location_capacity_fences(uuid[]) FROM PUBLIC;

CREATE INDEX stock_part_outs_source_bin ON brickvault.stock_part_outs(source_location_id);

CREATE FUNCTION brickvault.check_location_capacity(bin uuid, incoming bigint, provisionally_consumed bigint)
RETURNS void LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE
    configured bigint;
    before_quantity bigint;
    net_increase bigint := incoming - provisionally_consumed;
BEGIN
    IF net_increase<=0 THEN RETURN; END IF;
    SELECT e.piece_limit INTO configured FROM brickvault.location_capacity_events e
    WHERE e.location_id=bin ORDER BY e.revision DESC LIMIT 1;
    IF configured IS NULL THEN RETURN; END IF;

    -- Unmaterialized transformations may already reduce the stock view in this transaction.
    -- Their input is still occupying space for every decision except its own approved net effect.
    SELECT s.physical_quantity + COALESCE((
        SELECT sum(p.quantity) FROM brickvault.stock_part_outs p
        WHERE p.source_location_id=bin AND NOT EXISTS (
            SELECT 1 FROM brickvault.stock_part_out_outputs o
            JOIN brickvault.placements materialized ON materialized.id=o.position_id
            WHERE o.part_out_id=p.id
        )
    ),0) INTO before_quantity FROM brickvault.location_physical_stock(bin) s;
    IF before_quantity > configured-net_increase THEN
        -- A fixed-key machine witness of the locked decision. Never include user-controlled strings.
        RAISE check_violation USING CONSTRAINT='location_capacity_exceeded', MESSAGE=jsonb_build_object(
            'locationId',bin,'pieceLimit',configured,'physicalQuantity',before_quantity,
            'remainingQuantity',GREATEST(configured-before_quantity,0),'requestedNetQuantity',net_increase)::text;
    END IF;
END;
$$;
REVOKE ALL ON FUNCTION brickvault.check_location_capacity(uuid,bigint,bigint) FROM PUBLIC;

CREATE FUNCTION brickvault.capture_location_capacity_event() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    current_revision integer;
BEGIN
    SELECT revision INTO current_revision FROM brickvault.locations WHERE id=NEW.location_id FOR UPDATE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='unknown_location'; END IF;
    IF NEW.revision<>current_revision+1 THEN RAISE check_violation USING CONSTRAINT='location_registry_revision'; END IF;
    PERFORM brickvault.lock_location_capacity_fences(ARRAY[NEW.location_id]);

    SELECT e.piece_limit INTO NEW.previous_piece_limit FROM brickvault.location_capacity_events e
    WHERE e.location_id=NEW.location_id ORDER BY e.revision DESC LIMIT 1;
    SELECT s.physical_quantity,s.position_count INTO NEW.physical_quantity,NEW.position_count
    FROM brickvault.location_physical_stock(NEW.location_id) s;
    UPDATE brickvault.locations SET revision=NEW.revision WHERE id=NEW.location_id;
    RETURN NEW;
END;
$$;
CREATE TRIGGER capture_location_capacity_event BEFORE INSERT ON brickvault.location_capacity_events
    FOR EACH ROW EXECUTE FUNCTION brickvault.capture_location_capacity_event();
REVOKE ALL ON FUNCTION brickvault.capture_location_capacity_event() FROM PUBLIC;

CREATE FUNCTION brickvault.require_location_capacity_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.commands c JOIN brickvault.audit a USING(command_id)
        WHERE c.command_id=NEW.command_id AND c.entity_id=NEW.id AND c.revision=NEW.revision AND c.kind='location-capacity'
    ) THEN RAISE check_violation USING CONSTRAINT='location_capacity_receipt_required'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER require_location_capacity_receipt AFTER INSERT ON brickvault.location_capacity_events
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_location_capacity_receipt();

CREATE FUNCTION brickvault.require_location_capacity_command() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.location_capacity_events e
        WHERE e.command_id=NEW.command_id AND e.id=NEW.entity_id AND e.revision=NEW.revision
    ) THEN RAISE check_violation USING CONSTRAINT='location_capacity_receipt_required'; END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER require_location_capacity_command AFTER INSERT ON brickvault.commands
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.kind='location-capacity')
    EXECUTE FUNCTION brickvault.require_location_capacity_command();

-- Existing inventory writes retain their archive and label checks. Only incoming net pieces are limited.

CREATE OR REPLACE FUNCTION brickvault.require_canonical_location_receipt() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    receipt uuid;
BEGIN
    IF TG_OP='UPDATE' AND NEW.code=OLD.code AND NEW.name=OLD.name AND EXISTS (
        SELECT 1 FROM brickvault.location_registry_events e
        WHERE e.location_id=NEW.id AND e.revision=NEW.revision AND e.kind IN ('label-added','archived')
          AND e.canonical_code=NEW.code AND e.name=NEW.name
    ) THEN RETURN NULL; END IF;

    IF TG_OP='UPDATE' AND NEW.code=OLD.code AND NEW.name=OLD.name AND EXISTS (
        SELECT 1 FROM brickvault.location_capacity_events e
        WHERE e.location_id=NEW.id AND e.revision=NEW.revision
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

CREATE OR REPLACE FUNCTION brickvault.guard_placement() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
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
    -- Trusted children are already included in one parent-wide capacity decision. A normal
    -- placement cannot adopt a child identity, command or layer from a different action.
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.stock_part_out_outputs o
        JOIN brickvault.stock_part_outs p ON p.id=o.part_out_id
        JOIN brickvault.commands c ON c.command_id=o.child_command_id
        WHERE o.position_id=NEW.id AND o.delivery_id=NEW.delivery_id AND o.child_command_id=NEW.command_id
            AND c.kind='part-out-output' AND c.entity_id=o.delivery_id AND p.purchase_id=NEW.purchase_id
            AND o.quantity=NEW.quantity AND o.catalog_id=NEW.catalog_id AND o.catalog_revision=NEW.catalog_revision
            AND o.location_id=NEW.location_id AND o.location_code=NEW.location_code
    ) THEN
        PERFORM brickvault.lock_location_capacity_fences(ARRAY[NEW.location_id]);
        PERFORM brickvault.check_location_capacity(NEW.location_id,NEW.quantity,0);
    END IF;
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
    PERFORM brickvault.lock_location_capacity_fences(ARRAY[NEW.source_location_id,NEW.destination_location_id]);
    PERFORM brickvault.check_location_capacity(NEW.destination_location_id,NEW.quantity,0);
    INSERT INTO brickvault.placement_positions
        (id,origin_placement_id,purchase_id,catalog_id,catalog_revision,location_id,location_code,introduced_quantity)
    VALUES (NEW.id,source.origin_placement_id,source.purchase_id,source.catalog_id,source.catalog_revision,
        NEW.destination_location_id,NEW.destination_location_code,NEW.quantity);
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION brickvault.require_stock_part_out() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    source_digest bytea;
    produced bigint;
    same_identity_quantity bigint;
    described bigint;
    allocated bigint;
    known_outputs bigint;
    destination record;
BEGIN
    SELECT c.digest INTO source_digest FROM brickvault.commands c JOIN brickvault.audit a USING(command_id)
    WHERE c.command_id=NEW.command_id AND c.kind='part-out' AND c.entity_id=NEW.id
        AND c.revision-1=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='part_out_receipt_required'; END IF;
    SELECT COALESCE(sum(quantity),0),COALESCE(sum(item_cost_minor),0),count(item_cost_minor),
        COALESCE(sum(quantity) FILTER (WHERE catalog_id=NEW.source_catalog_id),0)
    INTO produced,allocated,known_outputs,same_identity_quantity
    FROM brickvault.stock_part_out_outputs WHERE part_out_id=NEW.id;
    SELECT COALESCE(sum(quantity),0) INTO described FROM brickvault.stock_part_out_residuals WHERE part_out_id=NEW.id;
    IF same_identity_quantity>NEW.quantity THEN
        RAISE check_violation USING CONSTRAINT='part_out_identity_gain';
    END IF;
    IF produced NOT BETWEEN 1 AND 1000000 OR described>1000000 THEN
        RAISE check_violation USING CONSTRAINT='part_out_amount';
    END IF;
    IF (NEW.source_item_cost_minor IS NULL AND known_outputs>0)
        OR (NEW.source_item_cost_minor IS NOT NULL AND allocated+NEW.residual_item_cost_minor<>NEW.source_item_cost_minor) THEN
        RAISE check_violation USING CONSTRAINT='part_out_cost_mismatch';
    END IF;

    -- Source and output location locks were taken during capture. Acquire the entire stock
    -- lock set before calculating room so two batches crossing several bins cannot deadlock.
    PERFORM brickvault.lock_location_capacity_fences(ARRAY(
        SELECT NEW.source_location_id
        UNION
        SELECT location_id FROM brickvault.stock_part_out_outputs WHERE part_out_id=NEW.id
    ));
    FOR destination IN
        SELECT location_id,sum(quantity) AS quantity FROM brickvault.stock_part_out_outputs
        WHERE part_out_id=NEW.id GROUP BY location_id ORDER BY location_id
    LOOP
        PERFORM brickvault.check_location_capacity(destination.location_id,destination.quantity,
            CASE WHEN NEW.source_location_id=destination.location_id THEN NEW.quantity ELSE 0 END);
    END LOOP;

    -- Each child receipt is a genuine typed output from this signed transformation.
    INSERT INTO brickvault.commands(command_id,kind,digest,entity_id,revision,correlation_id)
    SELECT child_command_id,'part-out-output',sha256(source_digest||uuid_send(line_id)),delivery_id,
        NEW.expected_purchase_revision+1,gen_random_uuid()
    FROM brickvault.stock_part_out_outputs WHERE part_out_id=NEW.id ORDER BY line_id;
    INSERT INTO brickvault.audit(command_id,actor)
    SELECT child_command_id,'operator' FROM brickvault.stock_part_out_outputs WHERE part_out_id=NEW.id ORDER BY line_id;

    INSERT INTO brickvault.deliveries(id,command_id,purchase_id,reference,quantity,catalog_id,catalog_revision,item_cost_minor)
    SELECT delivery_id,child_command_id,NEW.purchase_id,'Part-out '||delivery_id::text,
        quantity,catalog_id,catalog_revision,item_cost_minor
    FROM brickvault.stock_part_out_outputs WHERE part_out_id=NEW.id ORDER BY line_id;
    INSERT INTO brickvault.placements(id,command_id,purchase_id,delivery_id,catalog_id,catalog_revision,
        confirmed_catalog_revision,location_id,location_code,quantity)
    SELECT position_id,child_command_id,NEW.purchase_id,delivery_id,catalog_id,catalog_revision,
        catalog_revision,location_id,location_code,quantity
    FROM brickvault.stock_part_out_outputs WHERE part_out_id=NEW.id ORDER BY line_id;

    INSERT INTO brickvault.stock_quarantines(id,command_id,purchase_id,position_id,location_id,location_code,
        catalog_id,catalog_revision,recorded_catalog,expected_purchase_revision,quantity,reason)
    SELECT position_id,child_command_id,NEW.purchase_id,position_id,location_id,location_code,catalog_id,catalog_revision,
        recorded_catalog,NEW.expected_purchase_revision,quantity,NEW.reason
    FROM brickvault.stock_part_out_outputs WHERE part_out_id=NEW.id AND disposition='retained' ORDER BY line_id;
    RETURN NULL;
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
    PERFORM brickvault.lock_location_capacity_fences(ARRAY[NEW.location_id]);
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

CREATE OR REPLACE FUNCTION brickvault.capture_stock_loss_recovery() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
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
    PERFORM brickvault.lock_location_capacity_fences(ARRAY[NEW.location_id]);
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
