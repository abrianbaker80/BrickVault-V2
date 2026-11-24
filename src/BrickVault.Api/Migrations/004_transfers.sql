ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check
    CHECK (kind IN ('catalog','location','purchase','delivery','putaway','offer','transfer'));

-- Every physical position carries the immutable lineage of its original putaway.
-- Its introduced quantity stays fixed. Outgoing movements determine its remaining quantity.
CREATE TABLE brickvault.placement_positions (
    id uuid PRIMARY KEY,
    origin_placement_id uuid NOT NULL,
    purchase_id uuid NOT NULL,
    catalog_id uuid NOT NULL,
    catalog_revision integer NOT NULL CHECK (catalog_revision > 0),
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    introduced_quantity integer NOT NULL CHECK (introduced_quantity BETWEEN 1 AND 1000000),
    UNIQUE (purchase_id,id),
    UNIQUE (purchase_id,id,catalog_id,catalog_revision),
    FOREIGN KEY (purchase_id,origin_placement_id,catalog_id,catalog_revision)
        REFERENCES brickvault.placements(purchase_id,id,catalog_id,catalog_revision)
);

INSERT INTO brickvault.placement_positions
    (id,origin_placement_id,purchase_id,catalog_id,catalog_revision,location_id,location_code,introduced_quantity)
SELECT id,id,purchase_id,catalog_id,catalog_revision,location_id,location_code,quantity FROM brickvault.placements;

CREATE INDEX placement_positions_location ON brickvault.placement_positions(location_id);

CREATE TABLE brickvault.placement_transfers (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    source_position_id uuid NOT NULL,
    source_location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    source_location_code varchar(48) NOT NULL,
    destination_location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    destination_location_code varchar(48) NOT NULL,
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    reason varchar(200) NOT NULL CHECK (length(trim(reason)) > 0),
    CHECK (source_location_id <> destination_location_id AND source_position_id <> id),
    FOREIGN KEY (purchase_id,source_position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    FOREIGN KEY (purchase_id,id) REFERENCES brickvault.placement_positions(purchase_id,id)
);

CREATE INDEX placement_transfers_source ON brickvault.placement_transfers(source_position_id);
CREATE INDEX placement_transfers_from_bin ON brickvault.placement_transfers(source_location_id);
CREATE INDEX placement_transfers_to_bin ON brickvault.placement_transfers(destination_location_id);

CREATE TABLE brickvault.placement_transfer_backing (
    transfer_id uuid NOT NULL REFERENCES brickvault.placement_transfers(id),
    offer_id uuid NOT NULL REFERENCES brickvault.offers(id),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    PRIMARY KEY (transfer_id,offer_id)
);

-- Existing backing lines keep their exact placement IDs and now target both origin and moved positions.
DO $migration$
DECLARE
    old_fk name;
BEGIN
    SELECT conname INTO STRICT old_fk FROM pg_catalog.pg_constraint
    WHERE conrelid='brickvault.offer_backing'::regclass AND confrelid='brickvault.placements'::regclass
        AND contype='f';
    EXECUTE format('ALTER TABLE brickvault.offer_backing DROP CONSTRAINT %I', old_fk);
END;
$migration$;

ALTER TABLE brickvault.offer_backing ADD CONSTRAINT offer_backing_position_fk
    FOREIGN KEY (purchase_id,placement_id,catalog_id,catalog_revision)
    REFERENCES brickvault.placement_positions(purchase_id,id,catalog_id,catalog_revision);

CREATE VIEW brickvault.current_placement_stock AS
SELECT p.*,o.delivery_id,
    (p.introduced_quantity - COALESCE((SELECT sum(t.quantity) FROM brickvault.placement_transfers t
        WHERE t.source_position_id=p.id),0))::integer AS quantity
FROM brickvault.placement_positions p JOIN brickvault.placements o ON o.id=p.origin_placement_id;

CREATE VIEW brickvault.current_offer_backing AS
SELECT offer_id,placement_id,sum(quantity)::integer AS quantity
FROM (
    SELECT offer_id,placement_id,quantity FROM brickvault.offer_backing
    UNION ALL
    SELECT b.offer_id,t.id,b.quantity
    FROM brickvault.placement_transfer_backing b JOIN brickvault.placement_transfers t ON t.id=b.transfer_id
    UNION ALL
    SELECT b.offer_id,t.source_position_id,-b.quantity
    FROM brickvault.placement_transfer_backing b JOIN brickvault.placement_transfers t ON t.id=b.transfer_id
) ledger
GROUP BY offer_id,placement_id
HAVING sum(quantity) <> 0;

-- Existing putaway can only introduce its own initial position. The API has no direct INSERT on positions.
CREATE FUNCTION brickvault.seed_placement_position() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    INSERT INTO brickvault.placement_positions
        (id,origin_placement_id,purchase_id,catalog_id,catalog_revision,location_id,location_code,introduced_quantity)
    VALUES (NEW.id,NEW.id,NEW.purchase_id,NEW.catalog_id,NEW.catalog_revision,NEW.location_id,NEW.location_code,NEW.quantity);
    RETURN NEW;
END;
$$;

CREATE TRIGGER seed_placement_position AFTER INSERT ON brickvault.placements
    FOR EACH ROW EXECUTE FUNCTION brickvault.seed_placement_position();
REVOKE ALL ON FUNCTION brickvault.seed_placement_position() FROM PUBLIC;

-- Use the same purchase-row write fence as receiving and offer creation, including direct database writers.
CREATE FUNCTION brickvault.guard_placement_transfer() RETURNS trigger LANGUAGE plpgsql
    SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE
    source brickvault.current_placement_stock%ROWTYPE;
    confirmed integer;
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

    SELECT count(*) INTO confirmed FROM (
        SELECT id FROM brickvault.locations
        WHERE (id=NEW.source_location_id AND code=NEW.source_location_code)
           OR (id=NEW.destination_location_id AND code=NEW.destination_location_code)
        ORDER BY id FOR SHARE
    ) bins;
    IF confirmed <> 2 THEN
        RAISE check_violation USING CONSTRAINT='transfer_bin';
    END IF;
    INSERT INTO brickvault.placement_positions
        (id,origin_placement_id,purchase_id,catalog_id,catalog_revision,location_id,location_code,introduced_quantity)
    VALUES (NEW.id,source.origin_placement_id,source.purchase_id,source.catalog_id,source.catalog_revision,
        NEW.destination_location_id,NEW.destination_location_code,NEW.quantity);
    RETURN NEW;
END;
$$;

CREATE TRIGGER guard_placement_transfer BEFORE INSERT ON brickvault.placement_transfers
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_placement_transfer();
REVOKE ALL ON FUNCTION brickvault.guard_placement_transfer() FROM PUBLIC;

CREATE FUNCTION brickvault.guard_transfer_backing() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    movement brickvault.placement_transfers%ROWTYPE;
    remaining integer;
BEGIN
    SELECT * INTO movement FROM brickvault.placement_transfers WHERE id=NEW.transfer_id;
    IF NOT FOUND OR EXISTS (SELECT 1 FROM brickvault.commands WHERE command_id=movement.command_id) THEN
        RAISE check_violation USING CONSTRAINT='transfer_backing';
    END IF;
    SELECT quantity INTO remaining FROM brickvault.current_offer_backing
    WHERE offer_id=NEW.offer_id AND placement_id=movement.source_position_id;
    IF NOT FOUND OR NEW.quantity > remaining THEN
        RAISE check_violation USING CONSTRAINT='transfer_backing';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER guard_transfer_backing BEFORE INSERT ON brickvault.placement_transfer_backing
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_transfer_backing();

CREATE FUNCTION brickvault.require_transfer_consistency() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF COALESCE((SELECT sum(quantity) FROM brickvault.placement_transfer_backing
                 WHERE transfer_id=NEW.id),0) > NEW.quantity
        OR COALESCE((SELECT sum(quantity) FROM brickvault.current_offer_backing
                     WHERE placement_id=NEW.source_position_id),0)
           > (SELECT quantity FROM brickvault.current_placement_stock WHERE id=NEW.source_position_id) THEN
        RAISE check_violation USING CONSTRAINT='transfer_backing';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM brickvault.commands c
                   WHERE c.command_id=NEW.command_id AND c.kind='transfer' AND c.entity_id=NEW.id) THEN
        RAISE check_violation USING CONSTRAINT='transfer_source';
    END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER require_transfer_consistency AFTER INSERT ON brickvault.placement_transfers
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_transfer_consistency();

-- Creating a new offer uses current balances, including stock that arrived via a transfer.
CREATE OR REPLACE FUNCTION brickvault.guard_backing() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    placed integer;
BEGIN
    UPDATE brickvault.purchases SET revision=revision WHERE id=NEW.purchase_id;
    IF EXISTS (SELECT 1 FROM brickvault.offers o JOIN brickvault.commands c USING (command_id)
               WHERE o.id=NEW.offer_id) THEN
        RAISE check_violation USING CONSTRAINT='receiving_backing';
    END IF;
    SELECT quantity INTO placed FROM brickvault.current_placement_stock
    WHERE id=NEW.placement_id AND purchase_id=NEW.purchase_id
        AND catalog_id=NEW.catalog_id AND catalog_revision=NEW.catalog_revision;
    IF NOT FOUND THEN
        RAISE check_violation USING CONSTRAINT='receiving_backing';
    END IF;
    IF NEW.quantity + COALESCE((SELECT sum(quantity) FROM brickvault.current_offer_backing
                               WHERE placement_id=NEW.placement_id),0) > placed THEN
        RAISE check_violation USING CONSTRAINT='receiving_quantity';
    END IF;
    RETURN NEW;
END;
$$;
