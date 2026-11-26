ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check CHECK (kind IN
    ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release',
     'stock-loss','recount','offer-withdrawal','quarantine','quarantine-release','shortage-closure','stock-loss-recovery',
     'part-out','part-out-output'));

-- The existing purchase-revision trigger still serializes ordinary quarantine. Several child
-- positions can each receive one verified retained quarantine in the same part-out revision.
ALTER TABLE brickvault.stock_quarantines DROP CONSTRAINT stock_quarantines_purchase_id_expected_purchase_revision_key;

CREATE TABLE brickvault.stock_part_outs (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    source_position_id uuid NOT NULL,
    source_location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    source_location_code varchar(48) NOT NULL,
    source_catalog_id uuid NOT NULL REFERENCES brickvault.catalog(id),
    source_catalog_revision integer NOT NULL CHECK (source_catalog_revision>0),
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision>0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    residual_item_cost_minor integer CHECK (residual_item_cost_minor BETWEEN 0 AND 100000000),
    source_delivery_id uuid NOT NULL REFERENCES brickvault.deliveries(id),
    recorded_source_catalog jsonb NOT NULL,
    basis_slots int4multirange NOT NULL CHECK (NOT isempty(basis_slots)),
    source_item_cost_minor integer CHECK (source_item_cost_minor BETWEEN 0 AND 100000000),
    FOREIGN KEY (purchase_id,source_position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    UNIQUE (purchase_id,expected_purchase_revision)
);
CREATE INDEX stock_part_outs_position ON brickvault.stock_part_outs(source_position_id,expected_purchase_revision DESC);
CREATE INDEX stock_part_outs_delivery ON brickvault.stock_part_outs(source_delivery_id);

CREATE TABLE brickvault.stock_part_out_outputs (
    part_out_id uuid NOT NULL REFERENCES brickvault.stock_part_outs(id),
    line_id uuid NOT NULL,
    catalog_id uuid NOT NULL REFERENCES brickvault.catalog(id),
    catalog_revision integer NOT NULL CHECK (catalog_revision>0),
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    item_cost_minor integer CHECK (item_cost_minor BETWEEN 0 AND 100000000),
    disposition text NOT NULL CHECK (disposition IN ('stock','retained')),
    delivery_id uuid NOT NULL UNIQUE DEFAULT gen_random_uuid()
        REFERENCES brickvault.deliveries(id) DEFERRABLE INITIALLY DEFERRED,
    position_id uuid NOT NULL UNIQUE DEFAULT gen_random_uuid()
        REFERENCES brickvault.placement_positions(id) DEFERRABLE INITIALLY DEFERRED,
    child_command_id uuid NOT NULL UNIQUE DEFAULT gen_random_uuid()
        REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    recorded_catalog jsonb NOT NULL,
    PRIMARY KEY (part_out_id,line_id)
);

CREATE TABLE brickvault.stock_part_out_residuals (
    part_out_id uuid NOT NULL REFERENCES brickvault.stock_part_outs(id),
    line_id uuid NOT NULL,
    disposition text NOT NULL CHECK (disposition IN ('discarded','lost')),
    description varchar(200) NOT NULL CHECK (length(trim(description))>0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    PRIMARY KEY (part_out_id,line_id)
);

CREATE TABLE brickvault.stock_loss_part_out_context (
    loss_id uuid PRIMARY KEY REFERENCES brickvault.stock_loss_postings(id) DEFERRABLE INITIALLY DEFERRED,
    prior_transformed_quantity integer NOT NULL CHECK (prior_transformed_quantity BETWEEN 1 AND 1000000),
    prior_transformed_item_cost_minor integer CHECK (prior_transformed_item_cost_minor BETWEEN 0 AND 100000000)
);

CREATE VIEW brickvault.delivery_part_out_balances AS
SELECT d.id AS delivery_id,child.part_out_id AS parent_transformation_id,
    parent.source_delivery_id,parent.source_position_id,used.quantity AS transformed_out_quantity,
    CASE WHEN d.item_cost_minor IS NOT NULL THEN used.basis END AS transformed_out_item_cost_minor
FROM brickvault.deliveries d
LEFT JOIN brickvault.stock_part_out_outputs child ON child.delivery_id=d.id
LEFT JOIN brickvault.stock_part_outs parent ON parent.id=child.part_out_id
CROSS JOIN LATERAL (
    SELECT COALESCE(sum(p.quantity),0)::integer AS quantity,
        COALESCE(sum(p.source_item_cost_minor),0)::integer AS basis
    FROM brickvault.stock_part_outs p WHERE p.source_delivery_id=d.id
) used;

CREATE FUNCTION brickvault.open_stock_basis_slots(layer uuid)
RETURNS int4multirange LANGUAGE sql STABLE STRICT SET search_path=pg_catalog AS $$
    SELECT int4multirange(int4range(0,d.quantity,'[)'))
        - COALESCE((SELECT range_agg(b.basis_slots) FROM brickvault.active_stock_loss_basis b WHERE b.delivery_id=d.id),'{}'::int4multirange)
        - COALESCE((SELECT range_agg(p.basis_slots) FROM brickvault.stock_part_outs p WHERE p.source_delivery_id=d.id),'{}'::int4multirange)
    FROM brickvault.deliveries d WHERE d.id=layer;
$$;

CREATE FUNCTION brickvault.part_out_catalog_snapshot(c brickvault.catalog)
RETURNS jsonb LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
    SELECT jsonb_build_object('id',c.id,'revision',c.revision,'itemType',c.item_type,'itemNumber',c.item_number,
        'variant',c.variant,'name',c.name,'colorId',c.color_id,'colorState',c.color_state,'condition',c.condition,
        'completeness',c.completeness,'source',c.source,'license',c.license,'mapping',c.mapping,
        'integrationEligible',c.integration_eligible);
$$;

CREATE OR REPLACE VIEW brickvault.current_placement_stock AS
SELECT p.*,o.delivery_id,
    (p.introduced_quantity
        - COALESCE((SELECT sum(t.quantity) FROM brickvault.placement_transfers t WHERE t.source_position_id=p.id),0)
        - COALESCE((SELECT sum(l.lost_quantity) FROM brickvault.stock_loss_postings l WHERE l.position_id=p.id),0)
        + COALESCE((SELECT sum(r.saleable_quantity+r.quarantine_quantity)
                    FROM brickvault.stock_loss_recoveries r WHERE r.position_id=p.id),0)
        - COALESCE((SELECT sum(x.quantity) FROM brickvault.stock_part_outs x WHERE x.source_position_id=p.id),0))::integer AS quantity
FROM brickvault.placement_positions p JOIN brickvault.placements o ON o.id=p.origin_placement_id;

CREATE OR REPLACE VIEW brickvault.stock_position_physical_deltas AS
SELECT t.source_position_id AS position_id,c.revision,-t.quantity AS delta
FROM brickvault.placement_transfers t JOIN brickvault.commands c ON c.command_id=t.command_id
UNION ALL
SELECT l.position_id,c.revision,-l.lost_quantity
FROM brickvault.stock_loss_postings l JOIN brickvault.commands c ON c.command_id=l.command_id
UNION ALL
SELECT r.position_id,c.revision,r.saleable_quantity+r.quarantine_quantity
FROM brickvault.stock_loss_recoveries r JOIN brickvault.commands c ON c.command_id=r.command_id
UNION ALL
SELECT p.source_position_id,c.revision,-p.quantity
FROM brickvault.stock_part_outs p JOIN brickvault.commands c ON c.command_id=p.command_id;

CREATE FUNCTION brickvault.capture_stock_part_out() RETURNS trigger LANGUAGE plpgsql AS $$
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
    PERFORM 1 FROM brickvault.locations WHERE id=NEW.source_location_id AND code=NEW.source_location_code FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='part_out_bin'; END IF;
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
CREATE TRIGGER capture_stock_part_out BEFORE INSERT ON brickvault.stock_part_outs
    FOR EACH ROW EXECUTE FUNCTION brickvault.capture_stock_part_out();

CREATE FUNCTION brickvault.guard_stock_part_out_line() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    catalog brickvault.catalog%ROWTYPE;
BEGIN
    IF EXISTS (
        SELECT 1 FROM brickvault.stock_part_outs p JOIN brickvault.commands c ON c.command_id=p.command_id
        WHERE p.id=NEW.part_out_id
    ) THEN RAISE check_violation USING CONSTRAINT='part_out_amount'; END IF;
    IF TG_TABLE_NAME='stock_part_out_outputs' THEN
        SELECT * INTO catalog FROM brickvault.catalog
        WHERE id=NEW.catalog_id AND revision=NEW.catalog_revision AND integration_eligible FOR SHARE;
        IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='part_out_catalog'; END IF;
        PERFORM 1 FROM brickvault.locations WHERE id=NEW.location_id AND code=NEW.location_code FOR SHARE;
        IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='part_out_bin'; END IF;
        NEW.recorded_catalog := brickvault.part_out_catalog_snapshot(catalog);
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER guard_stock_part_out_output BEFORE INSERT ON brickvault.stock_part_out_outputs
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_stock_part_out_line();
CREATE TRIGGER guard_stock_part_out_residual BEFORE INSERT ON brickvault.stock_part_out_residuals
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_stock_part_out_line();

CREATE FUNCTION brickvault.require_stock_part_out() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    source_digest bytea;
    produced bigint;
    described bigint;
    allocated bigint;
    known_outputs bigint;
BEGIN
    SELECT c.digest INTO source_digest FROM brickvault.commands c JOIN brickvault.audit a USING(command_id)
    WHERE c.command_id=NEW.command_id AND c.kind='part-out' AND c.entity_id=NEW.id
        AND c.revision-1=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='part_out_receipt_required'; END IF;
    SELECT COALESCE(sum(quantity),0),COALESCE(sum(item_cost_minor),0),count(item_cost_minor)
    INTO produced,allocated,known_outputs FROM brickvault.stock_part_out_outputs WHERE part_out_id=NEW.id;
    SELECT COALESCE(sum(quantity),0) INTO described FROM brickvault.stock_part_out_residuals WHERE part_out_id=NEW.id;
    IF produced NOT BETWEEN 1 AND 1000000 OR described>1000000 THEN
        RAISE check_violation USING CONSTRAINT='part_out_amount';
    END IF;
    IF (NEW.source_item_cost_minor IS NULL AND known_outputs>0)
        OR (NEW.source_item_cost_minor IS NOT NULL AND allocated+NEW.residual_item_cost_minor<>NEW.source_item_cost_minor) THEN
        RAISE check_violation USING CONSTRAINT='part_out_cost_mismatch';
    END IF;

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
CREATE CONSTRAINT TRIGGER require_stock_part_out AFTER INSERT ON brickvault.stock_part_outs
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_stock_part_out();
REVOKE ALL ON FUNCTION brickvault.require_stock_part_out() FROM PUBLIC;

CREATE FUNCTION brickvault.require_stock_part_out_command() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.kind='part-out' THEN
        IF EXISTS (SELECT 1 FROM brickvault.stock_part_outs p JOIN brickvault.audit a ON a.command_id=p.command_id
            WHERE p.id=NEW.entity_id AND p.command_id=NEW.command_id AND p.expected_purchase_revision+1=NEW.revision)
        THEN RETURN NULL; END IF;
    ELSIF EXISTS (
        SELECT 1 FROM brickvault.stock_part_out_outputs o JOIN brickvault.stock_part_outs p ON p.id=o.part_out_id
        JOIN brickvault.audit a ON a.command_id=o.child_command_id
        JOIN brickvault.deliveries d ON d.id=o.delivery_id AND d.command_id=o.child_command_id
        JOIN brickvault.placements f ON f.id=o.position_id AND f.command_id=o.child_command_id AND f.delivery_id=d.id
        WHERE o.child_command_id=NEW.command_id AND d.id=NEW.entity_id AND p.expected_purchase_revision+1=NEW.revision
    ) THEN RETURN NULL; END IF;
    RAISE check_violation USING CONSTRAINT='part_out_receipt_required';
END;
$$;
CREATE CONSTRAINT TRIGGER require_stock_part_out_command AFTER INSERT ON brickvault.commands
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.kind IN ('part-out','part-out-output'))
    EXECUTE FUNCTION brickvault.require_stock_part_out_command();

CREATE OR REPLACE FUNCTION brickvault.guard_delivery() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ordered integer;
BEGIN
    IF EXISTS (
        SELECT 1 FROM brickvault.stock_part_out_outputs o JOIN brickvault.stock_part_outs p ON p.id=o.part_out_id
        JOIN brickvault.commands c ON c.command_id=o.child_command_id
        WHERE o.delivery_id=NEW.id AND o.child_command_id=NEW.command_id AND c.kind='part-out-output'
            AND c.entity_id=NEW.id AND p.purchase_id=NEW.purchase_id AND o.quantity=NEW.quantity
            AND o.catalog_id=NEW.catalog_id AND o.catalog_revision=NEW.catalog_revision
            AND o.item_cost_minor IS NOT DISTINCT FROM NEW.item_cost_minor
    ) THEN RETURN NEW; END IF;

    UPDATE brickvault.purchases SET revision = revision
    WHERE id = NEW.purchase_id RETURNING ordered_quantity INTO ordered;
    IF NEW.quantity + COALESCE((SELECT sum(d.quantity) FROM brickvault.deliveries d
                               WHERE d.purchase_id=NEW.purchase_id AND NOT EXISTS (
                                   SELECT 1 FROM brickvault.stock_part_out_outputs o WHERE o.delivery_id=d.id)), 0) > ordered THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_quantity';
    END IF;
    IF NEW.catalog_id IS NOT NULL THEN
        SELECT revision INTO NEW.catalog_revision FROM brickvault.catalog
        WHERE id = NEW.catalog_id FOR SHARE;
        IF NOT FOUND THEN
            RAISE check_violation USING CONSTRAINT = 'receiving_catalog';
        END IF;
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
    PERFORM 1 FROM brickvault.locations WHERE id=NEW.location_id AND code=NEW.location_code FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='stock_loss_bin_confirmation_mismatch'; END IF;
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

REVOKE ALL ON FUNCTION brickvault.capture_stock_loss() FROM PUBLIC;

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


CREATE OR REPLACE FUNCTION brickvault.require_stock_quarantine_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    required_kind text := CASE WHEN TG_TABLE_NAME='stock_quarantines' THEN 'quarantine' ELSE 'quarantine-release' END;
BEGIN
    IF required_kind='quarantine' AND EXISTS (
        SELECT 1 FROM brickvault.stock_part_out_outputs o
        JOIN brickvault.stock_part_outs p ON p.id=o.part_out_id JOIN brickvault.commands c ON c.command_id=o.child_command_id
        JOIN brickvault.audit a ON a.command_id=c.command_id
        WHERE o.position_id=NEW.id AND o.child_command_id=NEW.command_id AND c.kind='part-out-output'
            AND c.entity_id=o.delivery_id AND c.revision-1=NEW.expected_purchase_revision
            AND o.disposition='retained' AND o.quantity=NEW.quantity
    ) THEN RETURN NULL;
    END IF;
    IF required_kind='quarantine' AND EXISTS (
        SELECT 1 FROM brickvault.stock_loss_recoveries r JOIN brickvault.commands c USING(command_id)
        JOIN brickvault.audit a USING(command_id)
        WHERE r.id=NEW.id AND r.command_id=NEW.command_id AND c.kind='stock-loss-recovery'
            AND c.entity_id=r.id AND c.revision-1=NEW.expected_purchase_revision AND r.quarantine_quantity=NEW.quantity
    ) THEN RETURN NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM brickvault.commands c JOIN brickvault.audit a USING (command_id)
                   WHERE c.command_id=NEW.command_id AND c.kind=required_kind AND c.entity_id=NEW.id
                     AND c.revision-1=NEW.expected_purchase_revision) THEN
        RAISE check_violation USING CONSTRAINT='quarantine_receipt_required';
    END IF;
    RETURN NULL;
END;
$$;
