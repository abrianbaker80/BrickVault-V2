ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check
    CHECK (kind IN ('catalog','location','purchase','delivery','putaway','offer'));

CREATE TABLE brickvault.purchases (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.commands(command_id) DEFERRABLE INITIALLY DEFERRED,
    revision integer NOT NULL CHECK (revision > 0),
    reference varchar(64) NOT NULL UNIQUE CHECK (length(trim(reference)) > 0),
    source varchar(160) NOT NULL CHECK (length(trim(source)) > 0),
    currency varchar(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    ordered_quantity integer NOT NULL CHECK (ordered_quantity BETWEEN 1 AND 1000000),
    pending_charge_minor integer CHECK (pending_charge_minor BETWEEN 0 AND 100000000)
);

CREATE TABLE brickvault.deliveries (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.commands(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL REFERENCES brickvault.purchases(id),
    reference varchar(64) NOT NULL CHECK (length(trim(reference)) > 0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    catalog_id uuid REFERENCES brickvault.catalog(id),
    catalog_revision integer CHECK (catalog_revision > 0),
    item_cost_minor integer CHECK (item_cost_minor BETWEEN 0 AND 100000000),
    CHECK ((catalog_id IS NULL) = (catalog_revision IS NULL)),
    UNIQUE (purchase_id, reference),
    UNIQUE (purchase_id, id)
);

CREATE TABLE brickvault.placements (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.commands(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL,
    delivery_id uuid NOT NULL,
    catalog_id uuid NOT NULL REFERENCES brickvault.catalog(id),
    catalog_revision integer NOT NULL CHECK (catalog_revision > 0),
    confirmed_catalog_revision integer CHECK (confirmed_catalog_revision = catalog_revision),
    location_id uuid NOT NULL REFERENCES brickvault.locations(id),
    location_code varchar(48) NOT NULL,
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    FOREIGN KEY (purchase_id, delivery_id) REFERENCES brickvault.deliveries(purchase_id, id),
    UNIQUE (purchase_id, id, catalog_id, catalog_revision)
);

CREATE INDEX placements_delivery ON brickvault.placements(delivery_id);

CREATE TABLE brickvault.offers (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.commands(command_id) DEFERRABLE INITIALLY DEFERRED,
    purchase_id uuid NOT NULL REFERENCES brickvault.purchases(id),
    catalog_id uuid NOT NULL REFERENCES brickvault.catalog(id),
    catalog_revision integer NOT NULL CHECK (catalog_revision > 0),
    unit_price_minor integer NOT NULL CHECK (unit_price_minor BETWEEN 1 AND 100000000),
    UNIQUE (purchase_id, id, catalog_id, catalog_revision)
);

CREATE TABLE brickvault.offer_backing (
    purchase_id uuid NOT NULL,
    offer_id uuid NOT NULL,
    placement_id uuid NOT NULL,
    catalog_id uuid NOT NULL,
    catalog_revision integer NOT NULL,
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 1000000),
    PRIMARY KEY (offer_id, placement_id),
    FOREIGN KEY (purchase_id, offer_id, catalog_id, catalog_revision)
        REFERENCES brickvault.offers(purchase_id, id, catalog_id, catalog_revision),
    FOREIGN KEY (purchase_id, placement_id, catalog_id, catalog_revision)
        REFERENCES brickvault.placements(purchase_id, id, catalog_id, catalog_revision)
);

CREATE INDEX offer_backing_placement ON brickvault.offer_backing(placement_id);

-- Every receiving write versions the parent row before computing availability.
-- The guard's update also fences transactions that attempt inserts outside the application.
CREATE FUNCTION brickvault.guard_delivery() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    ordered integer;
BEGIN
    UPDATE brickvault.purchases SET revision = revision
    WHERE id = NEW.purchase_id RETURNING ordered_quantity INTO ordered;
    IF NEW.quantity + COALESCE((SELECT sum(quantity) FROM brickvault.deliveries
                               WHERE purchase_id = NEW.purchase_id), 0) > ordered THEN
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

CREATE FUNCTION brickvault.guard_placement() RETURNS trigger LANGUAGE plpgsql AS $$
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
    WHERE id = NEW.catalog_id AND integration_eligible FOR SHARE;
    IF NOT FOUND OR (delivery.catalog_id IS NOT NULL AND delivery.catalog_id <> NEW.catalog_id) THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_catalog';
    END IF;
    -- A mixed receipt has no recorded catalog version to stand in for explicit operator confirmation.
    IF NEW.confirmed_catalog_revision IS DISTINCT FROM NEW.catalog_revision
        AND (NEW.confirmed_catalog_revision IS NOT NULL OR delivery.catalog_id IS NULL
            OR delivery.catalog_revision <> NEW.catalog_revision) THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_catalog_reconfirmation';
    END IF;
    PERFORM 1 FROM brickvault.locations
    WHERE id = NEW.location_id AND code = NEW.location_code FOR SHARE;
    IF NOT FOUND THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_bin';
    END IF;
    RETURN NEW;
END;
$$;

CREATE FUNCTION brickvault.guard_backing() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    placed integer;
BEGIN
    UPDATE brickvault.purchases SET revision = revision WHERE id = NEW.purchase_id;
    IF EXISTS (SELECT 1 FROM brickvault.offers o JOIN brickvault.commands c USING (command_id)
               WHERE o.id = NEW.offer_id) THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_backing';
    END IF;
    SELECT quantity INTO placed FROM brickvault.placements
    WHERE id = NEW.placement_id AND purchase_id = NEW.purchase_id
        AND catalog_id = NEW.catalog_id AND catalog_revision = NEW.catalog_revision;
    IF NOT FOUND THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_backing';
    END IF;
    IF NEW.quantity + COALESCE((SELECT sum(quantity) FROM brickvault.offer_backing
                               WHERE placement_id = NEW.placement_id), 0) > placed THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_quantity';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER guard_delivery BEFORE INSERT ON brickvault.deliveries
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_delivery();
CREATE TRIGGER guard_placement BEFORE INSERT ON brickvault.placements
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_placement();
CREATE TRIGGER guard_backing BEFORE INSERT ON brickvault.offer_backing
    FOR EACH ROW EXECUTE FUNCTION brickvault.guard_backing();

-- An unpublished offer and all its backing are one transaction.
CREATE FUNCTION brickvault.require_offer_backing() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM brickvault.offer_backing WHERE offer_id = NEW.id) THEN
        RAISE check_violation USING CONSTRAINT = 'receiving_backing';
    END IF;
    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER require_offer_backing AFTER INSERT ON brickvault.offers
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION brickvault.require_offer_backing();
