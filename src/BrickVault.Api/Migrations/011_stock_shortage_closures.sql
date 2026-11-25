ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check CHECK (kind IN
    ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release',
     'stock-loss','recount','offer-withdrawal','quarantine','quarantine-release','shortage-closure'));

CREATE TABLE brickvault.stock_shortage_closures (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    position_id uuid NOT NULL,
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    case_id uuid NOT NULL UNIQUE REFERENCES brickvault.stock_shortage_cases(id),
    observation_id uuid NOT NULL UNIQUE,
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision>0),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    ledger_quantity integer NOT NULL CHECK (ledger_quantity BETWEEN 1 AND 1000000),
    quarantined_quantity integer NOT NULL CHECK (quarantined_quantity BETWEEN 0 AND ledger_quantity),
    held_quantity integer NOT NULL CHECK (held_quantity BETWEEN 0 AND ledger_quantity),
    offer_backed_quantity integer NOT NULL,
    CHECK (offer_backed_quantity BETWEEN 0 AND ledger_quantity-quarantined_quantity-held_quantity),
    FOREIGN KEY (purchase_id,position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    FOREIGN KEY (position_id,observation_id) REFERENCES brickvault.stock_count_observations(position_id,id),
    UNIQUE (purchase_id,expected_purchase_revision)
);

CREATE INDEX stock_shortage_closures_position
    ON brickvault.stock_shortage_closures(position_id,expected_purchase_revision);

CREATE FUNCTION brickvault.capture_stock_shortage_closure() RETURNS trigger LANGUAGE plpgsql AS $$
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
    PERFORM 1 FROM brickvault.locations WHERE id=NEW.location_id AND code=NEW.location_code FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='shortage_closure_bin_conflict'; END IF;

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

CREATE TRIGGER capture_stock_shortage_closure BEFORE INSERT ON brickvault.stock_shortage_closures
    FOR EACH ROW EXECUTE FUNCTION brickvault.capture_stock_shortage_closure();

CREATE FUNCTION brickvault.require_stock_shortage_closure_receipt() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM brickvault.commands c JOIN brickvault.audit a USING(command_id)
        WHERE c.command_id=NEW.command_id AND c.kind='shortage-closure' AND c.entity_id=NEW.id
          AND c.revision-1=NEW.expected_purchase_revision
    ) THEN
        RAISE check_violation USING CONSTRAINT='shortage_closure_receipt_required';
    END IF;
    UPDATE brickvault.stock_shortage_cases SET resolved_by_command_id=NEW.command_id
    WHERE id=NEW.case_id AND position_id=NEW.position_id AND resolved_by_command_id IS NULL;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='shortage_closure_case_conflict'; END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER require_stock_shortage_closure_receipt AFTER INSERT ON brickvault.stock_shortage_closures
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_stock_shortage_closure_receipt();
REVOKE ALL ON FUNCTION brickvault.require_stock_shortage_closure_receipt() FROM PUBLIC;
