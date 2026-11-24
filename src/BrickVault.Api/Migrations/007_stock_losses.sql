ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check
    CHECK (kind IN ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release','stock-loss'));

CREATE TABLE brickvault.stock_loss_postings (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    position_id uuid NOT NULL,
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    case_id uuid NOT NULL UNIQUE REFERENCES brickvault.stock_shortage_cases(id),
    observation_id uuid NOT NULL UNIQUE,
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision > 0),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    delivery_id uuid NOT NULL REFERENCES brickvault.deliveries(id),
    ledger_quantity_before integer NOT NULL CHECK (ledger_quantity_before BETWEEN 1 AND 1000000),
    lost_quantity integer NOT NULL CHECK (lost_quantity BETWEEN 1 AND ledger_quantity_before),
    protected_project_quantity integer NOT NULL
        CHECK (protected_project_quantity BETWEEN 0 AND ledger_quantity_before-lost_quantity),
    offer_backed_quantity_before integer NOT NULL
        CHECK (offer_backed_quantity_before BETWEEN 0 AND ledger_quantity_before-protected_project_quantity),
    prior_delivery_loss_quantity integer NOT NULL CHECK (prior_delivery_loss_quantity BETWEEN 0 AND 1000000-lost_quantity),
    allocated_item_cost_minor integer CHECK (allocated_item_cost_minor >= 0),
    remaining_delivery_item_cost_minor integer CHECK (remaining_delivery_item_cost_minor >= 0),
    CHECK ((allocated_item_cost_minor IS NULL)=(remaining_delivery_item_cost_minor IS NULL)),
    FOREIGN KEY (purchase_id,position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    FOREIGN KEY (position_id,observation_id) REFERENCES brickvault.stock_count_observations(position_id,id),
    UNIQUE (purchase_id,expected_purchase_revision)
);

CREATE INDEX stock_loss_postings_position ON brickvault.stock_loss_postings(position_id);
CREATE INDEX stock_loss_postings_delivery ON brickvault.stock_loss_postings(delivery_id);

CREATE TABLE brickvault.stock_loss_offer_reductions (
    loss_id uuid NOT NULL REFERENCES brickvault.stock_loss_postings(id),
    offer_id uuid NOT NULL REFERENCES brickvault.offers(id),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    prior_backed_quantity integer,
    CONSTRAINT stock_loss_offer_reduction_mismatch CHECK (prior_backed_quantity >= quantity),
    PRIMARY KEY (loss_id,offer_id)
);

CREATE OR REPLACE VIEW brickvault.current_placement_stock AS
SELECT p.*,o.delivery_id,
    (p.introduced_quantity
        - COALESCE((SELECT sum(t.quantity) FROM brickvault.placement_transfers t WHERE t.source_position_id=p.id),0)
        - COALESCE((SELECT sum(l.lost_quantity) FROM brickvault.stock_loss_postings l WHERE l.position_id=p.id),0))::integer AS quantity
FROM brickvault.placement_positions p JOIN brickvault.placements o ON o.id=p.origin_placement_id;

CREATE OR REPLACE VIEW brickvault.current_offer_backing AS
SELECT offer_id,placement_id,sum(quantity)::integer AS quantity
FROM (
    SELECT offer_id,placement_id,quantity FROM brickvault.offer_backing
    UNION ALL
    SELECT b.offer_id,t.id,b.quantity
    FROM brickvault.placement_transfer_backing b JOIN brickvault.placement_transfers t ON t.id=b.transfer_id
    UNION ALL
    SELECT b.offer_id,t.source_position_id,-b.quantity
    FROM brickvault.placement_transfer_backing b JOIN brickvault.placement_transfers t ON t.id=b.transfer_id
    UNION ALL
    SELECT r.offer_id,l.position_id,-r.quantity
    FROM brickvault.stock_loss_offer_reductions r JOIN brickvault.stock_loss_postings l ON l.id=r.loss_id
) ledger
GROUP BY offer_id,placement_id
HAVING sum(quantity) <> 0;

CREATE FUNCTION brickvault.capture_stock_loss() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    stock brickvault.current_placement_stock%ROWTYPE;
    witness brickvault.stock_count_observations%ROWTYPE;
    delivery brickvault.deliveries%ROWTYPE;
    cumulative_cost integer;
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

    SELECT COALESCE(sum(lost_quantity),0)::integer INTO NEW.prior_delivery_loss_quantity
    FROM brickvault.stock_loss_postings WHERE delivery_id=NEW.delivery_id;
    SELECT * INTO STRICT delivery FROM brickvault.deliveries WHERE id=NEW.delivery_id;
    IF delivery.item_cost_minor IS NOT NULL THEN
        -- Cumulative rounding makes posting partitions irrelevant to the final loss basis.
        cumulative_cost := delivery.item_cost_minor::bigint *
            (NEW.prior_delivery_loss_quantity+NEW.lost_quantity) / delivery.quantity;
        NEW.allocated_item_cost_minor := cumulative_cost -
            (delivery.item_cost_minor::bigint * NEW.prior_delivery_loss_quantity / delivery.quantity);
        NEW.remaining_delivery_item_cost_minor := delivery.item_cost_minor-cumulative_cost;
    ELSE
        NEW.allocated_item_cost_minor := NULL;
        NEW.remaining_delivery_item_cost_minor := NULL;
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER capture_stock_loss BEFORE INSERT ON brickvault.stock_loss_postings
    FOR EACH ROW EXECUTE FUNCTION brickvault.capture_stock_loss();

CREATE FUNCTION brickvault.guard_stock_loss_reductions() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM loss_details r JOIN brickvault.stock_loss_postings l ON l.id=r.loss_id
        JOIN brickvault.commands c ON c.command_id=l.command_id
    ) THEN
        RAISE check_violation USING CONSTRAINT='stock_loss_offer_reduction_mismatch';
    END IF;
    RETURN NULL;
END;
$$;

CREATE TRIGGER guard_stock_loss_reductions AFTER INSERT ON brickvault.stock_loss_offer_reductions
    REFERENCING NEW TABLE AS loss_details FOR EACH STATEMENT EXECUTE FUNCTION brickvault.guard_stock_loss_reductions();

CREATE FUNCTION brickvault.require_stock_loss_completion() RETURNS trigger LANGUAGE plpgsql
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

    -- Reconstruct the immediately prior amount from the effective balance and this reduction.
    -- An offer absent from this position produces a negative residual and fails the detail constraint.
    UPDATE brickvault.stock_loss_offer_reductions r
    SET prior_backed_quantity=s.prior
    FROM (
        SELECT chosen.offer_id,COALESCE(b.quantity,0)+chosen.quantity AS prior
        FROM brickvault.stock_loss_offer_reductions chosen
        LEFT JOIN brickvault.current_offer_backing b
          ON b.offer_id=chosen.offer_id AND b.placement_id=NEW.position_id
        WHERE chosen.loss_id=NEW.id
    ) s WHERE r.loss_id=NEW.id AND r.offer_id=s.offer_id;

    UPDATE brickvault.stock_shortage_cases SET resolved_by_command_id=NEW.command_id
    WHERE id=NEW.case_id AND position_id=NEW.position_id AND resolved_by_command_id IS NULL;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='stock_loss_case_conflict'; END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER require_stock_loss_completion AFTER INSERT ON brickvault.stock_loss_postings
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_stock_loss_completion();
REVOKE ALL ON FUNCTION brickvault.require_stock_loss_completion() FROM PUBLIC;
