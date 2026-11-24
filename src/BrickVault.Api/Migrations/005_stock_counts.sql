ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check
    CHECK (kind IN ('catalog','location','purchase','delivery','putaway','offer','transfer','count'));

CREATE TABLE brickvault.stock_count_observations (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    position_id uuid NOT NULL,
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    expected_purchase_revision integer NOT NULL CHECK (expected_purchase_revision > 0),
    ledger_quantity integer NOT NULL CHECK (ledger_quantity BETWEEN 1 AND 1000000),
    offer_backed_quantity integer NOT NULL CHECK (offer_backed_quantity BETWEEN 0 AND ledger_quantity),
    counted_quantity integer NOT NULL CHECK (counted_quantity BETWEEN 0 AND 1000000),
    reason varchar(200) NOT NULL CHECK (length(trim(reason)) > 0),
    FOREIGN KEY (purchase_id,position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    UNIQUE (position_id,id),
    UNIQUE (purchase_id,expected_purchase_revision)
);

CREATE INDEX stock_count_observations_location ON brickvault.stock_count_observations(location_id);
CREATE INDEX stock_count_observations_position_revision
    ON brickvault.stock_count_observations(position_id,expected_purchase_revision DESC);

-- Its identity is the opening count. Later counts cannot close or replace it.
CREATE TABLE brickvault.stock_shortage_cases (
    id uuid PRIMARY KEY,
    position_id uuid NOT NULL UNIQUE,
    FOREIGN KEY (position_id,id) REFERENCES brickvault.stock_count_observations(position_id,id)
);

CREATE FUNCTION brickvault.capture_stock_count() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- The API has advanced this revision in the same transaction. The write also fences direct inserts.
    UPDATE brickvault.purchases SET revision=revision
    WHERE id=NEW.purchase_id AND revision-1=NEW.expected_purchase_revision;
    IF NOT FOUND THEN
        RAISE check_violation USING CONSTRAINT='count_revision';
    END IF;

    SELECT s.quantity INTO NEW.ledger_quantity
    FROM brickvault.current_placement_stock s
    WHERE s.id=NEW.position_id AND s.purchase_id=NEW.purchase_id AND s.location_id=NEW.location_id AND s.quantity>0;
    IF NOT FOUND OR EXISTS (SELECT 1 FROM brickvault.commands WHERE command_id=NEW.command_id) THEN
        RAISE check_violation USING CONSTRAINT='count_position';
    END IF;
    PERFORM 1 FROM brickvault.locations WHERE id=NEW.location_id AND code=NEW.location_code FOR SHARE;
    IF NOT FOUND THEN
        RAISE check_violation USING CONSTRAINT='count_bin';
    END IF;

    SELECT COALESCE(sum(quantity),0)::integer INTO NEW.offer_backed_quantity
    FROM brickvault.current_offer_backing WHERE placement_id=NEW.position_id;
    RETURN NEW;
END;
$$;

CREATE TRIGGER capture_stock_count BEFORE INSERT ON brickvault.stock_count_observations
    FOR EACH ROW EXECUTE FUNCTION brickvault.capture_stock_count();

CREATE FUNCTION brickvault.open_stock_shortage() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    INSERT INTO brickvault.stock_shortage_cases(id,position_id) VALUES (NEW.id,NEW.position_id)
    ON CONFLICT (position_id) DO NOTHING;
    RETURN NULL;
END;
$$;

CREATE TRIGGER open_stock_shortage AFTER INSERT ON brickvault.stock_count_observations
    FOR EACH ROW WHEN (NEW.counted_quantity < NEW.ledger_quantity) EXECUTE FUNCTION brickvault.open_stock_shortage();
REVOKE ALL ON FUNCTION brickvault.open_stock_shortage() FROM PUBLIC;

CREATE FUNCTION brickvault.require_stock_count_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM brickvault.commands
                   WHERE command_id=NEW.command_id AND kind='count' AND entity_id=NEW.id
                     AND revision-1=NEW.expected_purchase_revision) THEN
        RAISE check_violation USING CONSTRAINT='count_position';
    END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER require_stock_count_receipt AFTER INSERT ON brickvault.stock_count_observations
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_stock_count_receipt();

CREATE FUNCTION brickvault.guard_shortage_stock_use() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    affected_position uuid;
BEGIN
    IF TG_RELID = 'brickvault.offer_backing'::regclass THEN
        affected_position := NEW.placement_id;
    ELSE
        affected_position := NEW.source_position_id;
    END IF;
    IF EXISTS (SELECT 1 FROM brickvault.stock_shortage_cases WHERE position_id=affected_position) THEN
        RAISE check_violation USING CONSTRAINT='stock_shortage_open';
    END IF;
    RETURN NEW;
END;
$$;

-- The existing guards acquire the purchase write lock first (same-timing triggers run by name).
CREATE TRIGGER guard_shortage_stock_use BEFORE INSERT ON brickvault.offer_backing
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_shortage_stock_use();
CREATE TRIGGER guard_shortage_stock_use BEFORE INSERT ON brickvault.placement_transfers
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_shortage_stock_use();
