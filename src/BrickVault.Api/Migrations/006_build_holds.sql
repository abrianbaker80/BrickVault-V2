ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check
    CHECK (kind IN ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release'));

CREATE TABLE brickvault.stock_build_holds (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    position_id uuid NOT NULL,
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision > 0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    purpose varchar(120) NOT NULL CHECK (length(trim(purpose))>0),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    FOREIGN KEY (purchase_id,position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    UNIQUE (purchase_id,id),
    UNIQUE (purchase_id,expected_purchase_revision)
);

CREATE INDEX stock_build_holds_position ON brickvault.stock_build_holds(position_id,expected_purchase_revision DESC);

CREATE TABLE brickvault.stock_build_hold_releases (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    hold_id uuid NOT NULL,
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision > 0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    reason varchar(200) NOT NULL CHECK (length(trim(reason))>0),
    shortage_id uuid REFERENCES brickvault.stock_shortage_cases(id),
    expected_observation_id uuid REFERENCES brickvault.stock_count_observations(id),
    CHECK ((shortage_id IS NULL) = (expected_observation_id IS NULL)),
    FOREIGN KEY (purchase_id,hold_id) REFERENCES brickvault.stock_build_holds(purchase_id,id),
    UNIQUE (purchase_id,expected_purchase_revision)
);

CREATE INDEX stock_build_hold_releases_hold ON brickvault.stock_build_hold_releases(hold_id,expected_purchase_revision DESC);

CREATE VIEW brickvault.current_build_holds AS
SELECT h.*,released.quantity AS released_quantity,h.quantity-released.quantity AS remaining_quantity
FROM brickvault.stock_build_holds h
CROSS JOIN LATERAL (
    SELECT COALESCE(sum(quantity),0)::integer AS quantity
    FROM brickvault.stock_build_hold_releases WHERE hold_id=h.id
) released;

CREATE FUNCTION brickvault.guard_build_hold() RETURNS trigger LANGUAGE plpgsql AS $$
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
    PERFORM 1 FROM brickvault.locations WHERE id=NEW.location_id AND code=NEW.location_code FOR SHARE;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='hold_bin'; END IF;
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

CREATE TRIGGER guard_build_hold BEFORE INSERT ON brickvault.stock_build_holds
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_build_hold();

CREATE FUNCTION brickvault.guard_build_hold_release() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    held brickvault.current_build_holds%ROWTYPE;
    shortage uuid;
    latest uuid;
BEGIN
    UPDATE brickvault.purchases SET revision=revision+1
    WHERE id=NEW.purchase_id AND revision=NEW.expected_purchase_revision;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='hold_revision'; END IF;
    SELECT * INTO held FROM brickvault.current_build_holds
    WHERE id=NEW.hold_id AND purchase_id=NEW.purchase_id;
    IF NOT FOUND THEN RAISE check_violation USING CONSTRAINT='hold_position'; END IF;

    SELECT id INTO shortage FROM brickvault.stock_shortage_cases
    WHERE position_id=held.position_id AND resolved_by_command_id IS NULL;
    IF shortage IS NOT NULL THEN
        SELECT id INTO latest FROM brickvault.stock_count_observations WHERE position_id=held.position_id
        ORDER BY expected_purchase_revision DESC LIMIT 1;
    END IF;
    IF NEW.shortage_id IS DISTINCT FROM shortage OR NEW.expected_observation_id IS DISTINCT FROM latest THEN
        RAISE check_violation USING CONSTRAINT='hold_shortage_frontier';
    END IF;
    IF NEW.quantity > held.remaining_quantity THEN
        RAISE check_violation USING CONSTRAINT='hold_quantity';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER guard_build_hold_release BEFORE INSERT ON brickvault.stock_build_hold_releases
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_build_hold_release();

CREATE FUNCTION brickvault.require_build_hold_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    expected_kind text;
BEGIN
    expected_kind := CASE WHEN TG_RELID='brickvault.stock_build_holds'::regclass THEN 'hold' ELSE 'hold-release' END;
    IF NOT EXISTS (SELECT 1 FROM brickvault.commands
                   WHERE command_id=NEW.command_id AND kind=expected_kind AND entity_id=NEW.id
                     AND revision-1=NEW.expected_purchase_revision) THEN
        RAISE check_violation USING CONSTRAINT='hold_position';
    END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER require_build_hold_receipt AFTER INSERT ON brickvault.stock_build_holds
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_build_hold_receipt();
CREATE CONSTRAINT TRIGGER require_build_hold_receipt AFTER INSERT ON brickvault.stock_build_hold_releases
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_build_hold_receipt();

-- Existing triggers hold the purchase fence first. Apply the combined capacity invariant to both paths.
CREATE FUNCTION brickvault.guard_reserved_offer_backing() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.quantity + COALESCE((SELECT sum(quantity) FROM brickvault.current_offer_backing
                               WHERE placement_id=NEW.placement_id),0)
        + COALESCE((SELECT sum(remaining_quantity) FROM brickvault.current_build_holds
                    WHERE position_id=NEW.placement_id),0)
        > (SELECT quantity FROM brickvault.current_placement_stock WHERE id=NEW.placement_id) THEN
        RAISE check_violation USING CONSTRAINT='reserved_stock_unavailable';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER guard_z_reserved_offer_backing BEFORE INSERT ON brickvault.offer_backing
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_reserved_offer_backing();

CREATE FUNCTION brickvault.require_transfer_preserves_holds() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF COALESCE((SELECT sum(quantity) FROM brickvault.current_offer_backing
                 WHERE placement_id=NEW.source_position_id),0)
        + COALESCE((SELECT sum(remaining_quantity) FROM brickvault.current_build_holds
                    WHERE position_id=NEW.source_position_id),0)
        > (SELECT quantity FROM brickvault.current_placement_stock WHERE id=NEW.source_position_id) THEN
        RAISE check_violation USING CONSTRAINT='reserved_stock_unavailable';
    END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER require_transfer_preserves_holds AFTER INSERT ON brickvault.placement_transfers
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_transfer_preserves_holds();
