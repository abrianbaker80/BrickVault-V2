ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check CHECK (kind IN
    ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release',
     'stock-loss','recount','offer-withdrawal','quarantine','quarantine-release','shortage-closure','stock-loss-recovery'));

CREATE TABLE brickvault.stock_loss_basis_assignments (
    loss_id uuid PRIMARY KEY REFERENCES brickvault.stock_loss_postings(id) DEFERRABLE INITIALLY DEFERRED,
    basis_slots int4multirange NOT NULL CHECK (NOT isempty(basis_slots)),
    had_prior_recovery boolean NOT NULL
);

CREATE TABLE brickvault.stock_loss_recoveries (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    position_id uuid NOT NULL,
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    loss_id uuid NOT NULL REFERENCES brickvault.stock_loss_postings(id),
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision>0),
    catalog_id uuid NOT NULL REFERENCES brickvault.catalog(id),
    catalog_revision integer NOT NULL CHECK (catalog_revision>0),
    confirmed_identity boolean NOT NULL CHECK (confirmed_identity),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    recorded_catalog jsonb NOT NULL,
    saleable_quantity integer NOT NULL DEFAULT 0 CHECK (saleable_quantity BETWEEN 0 AND 1000000),
    quarantine_quantity integer NOT NULL DEFAULT 0 CHECK (quarantine_quantity BETWEEN 0 AND 1000000-saleable_quantity),
    restored_item_cost_minor integer CHECK (restored_item_cost_minor>=0),
    FOREIGN KEY (purchase_id,position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    UNIQUE (purchase_id,expected_purchase_revision)
);
CREATE INDEX stock_loss_recoveries_loss ON brickvault.stock_loss_recoveries(loss_id,expected_purchase_revision DESC);
CREATE INDEX stock_loss_recoveries_position ON brickvault.stock_loss_recoveries(position_id);

CREATE TABLE brickvault.stock_loss_recovery_allocations (
    recovery_id uuid NOT NULL REFERENCES brickvault.stock_loss_recoveries(id),
    quarantine_id uuid REFERENCES brickvault.stock_quarantines(id),
    saleable_quantity integer NOT NULL CHECK (saleable_quantity BETWEEN 0 AND 1000000),
    quarantine_quantity integer NOT NULL CHECK (quarantine_quantity BETWEEN 0 AND 1000000-saleable_quantity),
    CHECK (saleable_quantity+quarantine_quantity>0),
    basis_slots int4multirange NOT NULL DEFAULT '{}'::int4multirange,
    restored_item_cost_minor integer CHECK (restored_item_cost_minor>=0),
    UNIQUE NULLS NOT DISTINCT (recovery_id,quarantine_id)
);

-- Slice by the count of preceding slots, even when the original allocation has gaps.
CREATE FUNCTION brickvault.slice_basis_slots(slots int4multirange, skip_units integer, take_units integer)
RETURNS int4multirange LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
    SELECT COALESCE(range_agg(int4range(
        lower(span)+greatest(0,skip_units-preceding)::integer,
        lower(span)+least(upper(span)-lower(span),skip_units+take_units-preceding)::integer,
        '[)')), '{}'::int4multirange)
    FROM (
        SELECT span,COALESCE(sum(upper(span)-lower(span)) OVER
            (ORDER BY lower(span) ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING),0) AS preceding
        FROM unnest(slots) AS spans(span)
    ) ordered
    WHERE take_units>0 AND preceding<skip_units+take_units AND preceding+upper(span)-lower(span)>skip_units;
$$;

CREATE FUNCTION brickvault.basis_slot_amount(slots int4multirange, receipt_quantity integer, receipt_basis integer)
RETURNS integer LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
    SELECT COALESCE(sum(receipt_basis::bigint*upper(span)/receipt_quantity
        -receipt_basis::bigint*lower(span)/receipt_quantity),0)::integer
    FROM unnest(slots) AS spans(span);
$$;

CREATE VIEW brickvault.original_stock_loss_basis AS
SELECT l.id AS loss_id,l.delivery_id,COALESCE(a.basis_slots,
    int4multirange(int4range(l.prior_delivery_loss_quantity,l.prior_delivery_loss_quantity+l.lost_quantity,'[)'))) AS basis_slots
FROM brickvault.stock_loss_postings l
LEFT JOIN brickvault.stock_loss_basis_assignments a ON a.loss_id=l.id;

CREATE VIEW brickvault.active_stock_loss_basis AS
SELECT b.loss_id,b.delivery_id,b.basis_slots-COALESCE(returned.slots,'{}'::int4multirange) AS basis_slots
FROM brickvault.original_stock_loss_basis b
LEFT JOIN LATERAL (
    SELECT range_agg(a.basis_slots) AS slots FROM brickvault.stock_loss_recoveries r
    JOIN brickvault.stock_loss_recovery_allocations a ON a.recovery_id=r.id
    WHERE r.loss_id=b.loss_id
) returned ON true;

CREATE VIEW brickvault.delivery_loss_balances AS
SELECT d.id AS delivery_id,lost.quantity AS gross_lost_quantity,recovered.quantity AS recovered_quantity,
    (lost.quantity-recovered.quantity)::integer AS active_lost_quantity,
    CASE WHEN d.item_cost_minor IS NOT NULL THEN lost.basis END AS gross_allocated_item_cost_minor,
    CASE WHEN d.item_cost_minor IS NOT NULL THEN recovered.basis END AS restored_item_cost_minor,
    CASE WHEN d.item_cost_minor IS NOT NULL THEN (lost.basis-recovered.basis)::integer END AS active_allocated_item_cost_minor
FROM brickvault.deliveries d
CROSS JOIN LATERAL (
    SELECT COALESCE(sum(l.lost_quantity),0) AS quantity,COALESCE(sum(l.allocated_item_cost_minor::numeric),0) AS basis
    FROM brickvault.stock_loss_postings l WHERE l.delivery_id=d.id
) lost
CROSS JOIN LATERAL (
    SELECT COALESCE(sum(r.saleable_quantity+r.quarantine_quantity),0) AS quantity,
        COALESCE(sum(r.restored_item_cost_minor::numeric),0) AS basis
    FROM brickvault.stock_loss_postings l JOIN brickvault.stock_loss_recoveries r ON r.loss_id=l.id
    WHERE l.delivery_id=d.id
) recovered;

CREATE OR REPLACE VIEW brickvault.current_placement_stock AS
SELECT p.*,o.delivery_id,
    (p.introduced_quantity
        - COALESCE((SELECT sum(t.quantity) FROM brickvault.placement_transfers t WHERE t.source_position_id=p.id),0)
        - COALESCE((SELECT sum(l.lost_quantity) FROM brickvault.stock_loss_postings l WHERE l.position_id=p.id),0)
        + COALESCE((SELECT sum(r.saleable_quantity+r.quarantine_quantity)
                    FROM brickvault.stock_loss_recoveries r WHERE r.position_id=p.id),0))::integer AS quantity
FROM brickvault.placement_positions p JOIN brickvault.placements o ON o.id=p.origin_placement_id;

-- Split the immutable posting slots into origin buckets in ordinary-first, quarantine-id order.
CREATE FUNCTION brickvault.stock_loss_recovery_sources(posting uuid)
RETURNS TABLE(quarantine_id uuid,original_quantity integer,source_offset integer)
LANGUAGE sql STABLE STRICT SET search_path=pg_catalog AS $$
    SELECT source.id,source.quantity,COALESCE(sum(source.quantity) OVER
       (ORDER BY source.id NULLS FIRST ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING),0)::integer
    FROM (
        SELECT NULL::uuid AS id,l.lost_quantity-COALESCE((
            SELECT sum(a.quantity) FROM brickvault.stock_loss_quarantine_attributions a WHERE a.loss_id=l.id),0)::integer AS quantity
        FROM brickvault.stock_loss_postings l WHERE l.id=posting
        UNION ALL
        SELECT a.quarantine_id,a.quantity FROM brickvault.stock_loss_quarantine_attributions a WHERE a.loss_id=posting
    ) source;
$$;

CREATE FUNCTION brickvault.capture_stock_loss_recovery() RETURNS trigger LANGUAGE plpgsql AS $$
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
    PERFORM 1 FROM brickvault.locations WHERE id=NEW.location_id AND code=NEW.location_code FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='stock_loss_recovery_bin'; END IF;
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
CREATE TRIGGER capture_stock_loss_recovery BEFORE INSERT ON brickvault.stock_loss_recoveries
    FOR EACH ROW EXECUTE FUNCTION brickvault.capture_stock_loss_recovery();

CREATE FUNCTION brickvault.guard_stock_loss_recovery_allocations() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM new_allocations a JOIN brickvault.stock_loss_recoveries r ON r.id=a.recovery_id
        JOIN brickvault.commands c ON c.command_id=r.command_id
    ) THEN RAISE check_violation USING CONSTRAINT='stock_loss_recovery_amount'; END IF;
    RETURN NULL;
END;
$$;
CREATE TRIGGER guard_stock_loss_recovery_allocations AFTER INSERT ON brickvault.stock_loss_recovery_allocations
    REFERENCING NEW TABLE AS new_allocations FOR EACH STATEMENT EXECUTE FUNCTION brickvault.guard_stock_loss_recovery_allocations();

CREATE FUNCTION brickvault.require_stock_loss_recovery_receipt() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
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
    UPDATE brickvault.stock_loss_recoveries
    SET saleable_quantity=sold,quarantine_quantity=quarantined,restored_item_cost_minor=basis WHERE id=NEW.id;

    IF quarantined>0 THEN
        INSERT INTO brickvault.stock_quarantines(id,command_id,purchase_id,position_id,location_id,location_code,
            catalog_id,catalog_revision,recorded_catalog,expected_purchase_revision,quantity,reason)
        VALUES (NEW.id,NEW.command_id,NEW.purchase_id,NEW.position_id,NEW.location_id,NEW.location_code,
            NEW.catalog_id,NEW.catalog_revision,NEW.recorded_catalog,NEW.expected_purchase_revision,quarantined,NEW.reason);
    END IF;
    RETURN NULL;
END;
$$;
CREATE CONSTRAINT TRIGGER require_stock_loss_recovery_receipt AFTER INSERT ON brickvault.stock_loss_recoveries
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_stock_loss_recovery_receipt();
REVOKE ALL ON FUNCTION brickvault.require_stock_loss_recovery_receipt() FROM PUBLIC;

CREATE OR REPLACE FUNCTION brickvault.capture_stock_loss() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    stock brickvault.current_placement_stock%ROWTYPE;
    witness brickvault.stock_count_observations%ROWTYPE;
    delivery brickvault.deliveries%ROWTYPE;
    active_basis integer;
    had_recovery boolean;
    assigned int4multirange;
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
    SELECT brickvault.slice_basis_slots(int4multirange(int4range(0,delivery.quantity,'[)'))
        - COALESCE(range_agg(b.basis_slots),'{}'::int4multirange),0,NEW.lost_quantity) INTO assigned
    FROM brickvault.active_stock_loss_basis b WHERE b.delivery_id=NEW.delivery_id;
    IF (SELECT COALESCE(sum(upper(span)-lower(span)),0) FROM unnest(assigned) AS spans(span))<>NEW.lost_quantity THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_basis_unavailable';
    END IF;
    INSERT INTO brickvault.stock_loss_basis_assignments(loss_id,basis_slots,had_prior_recovery)
    VALUES (NEW.id,assigned,had_recovery);
    NEW.allocated_item_cost_minor := brickvault.basis_slot_amount(assigned,delivery.quantity,delivery.item_cost_minor);
    NEW.remaining_delivery_item_cost_minor := delivery.item_cost_minor-active_basis-NEW.allocated_item_cost_minor;
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

-- A queued task belongs to the units present at its frozen purchase revision. A zero balance
-- before its first witness is terminal even if a later recovery restores units to the position.
CREATE VIEW brickvault.stock_position_physical_deltas AS
SELECT t.source_position_id AS position_id,c.revision,-t.quantity AS delta
FROM brickvault.placement_transfers t JOIN brickvault.commands c ON c.command_id=t.command_id
UNION ALL
SELECT l.position_id,c.revision,-l.lost_quantity
FROM brickvault.stock_loss_postings l JOIN brickvault.commands c ON c.command_id=l.command_id
UNION ALL
SELECT r.position_id,c.revision,r.saleable_quantity+r.quarantine_quantity
FROM brickvault.stock_loss_recoveries r JOIN brickvault.commands c ON c.command_id=r.command_id;

CREATE VIEW brickvault.recount_task_states AS
SELECT r.batch_id,r.position_id,t.purchase_revision,
    CASE WHEN depleted.revision IS NULL THEN witness.id END AS observation_id,
    depleted.revision IS NOT NULL AS depleted
FROM brickvault.recount_results r
JOIN brickvault.recount_preview_targets t ON t.preview_id=r.batch_id AND t.position_id=r.position_id
LEFT JOIN LATERAL (
    SELECT n.id,n.expected_purchase_revision FROM brickvault.stock_count_observations n
    WHERE n.position_id=r.position_id AND n.expected_purchase_revision>=t.purchase_revision
    ORDER BY n.expected_purchase_revision,n.id LIMIT 1
) witness ON true
LEFT JOIN LATERAL (
    SELECT min(frontier.revision) AS revision
    FROM (
        SELECT physical.revision,t.quantity+sum(physical.delta) OVER (ORDER BY physical.revision) AS quantity
        FROM brickvault.stock_position_physical_deltas physical
        WHERE physical.position_id=r.position_id AND physical.revision>t.purchase_revision
            AND (witness.id IS NULL OR physical.revision<=witness.expected_purchase_revision)
    ) frontier
    WHERE frontier.quantity=0
) depleted ON true
WHERE r.outcome='queued';

CREATE OR REPLACE VIEW brickvault.recount_tasks AS
SELECT batch_id,position_id,purchase_revision,observation_id FROM brickvault.recount_task_states;
