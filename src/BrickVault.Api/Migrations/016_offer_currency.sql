-- Before independent pricing, all selling prices were entered in the purchase currency.
ALTER TABLE brickvault.offers ADD COLUMN currency varchar(3),
    ADD COLUMN price_scale_verified boolean NOT NULL DEFAULT false;
-- Old integrated browsers could calculate different minor units from the same typed price.
-- No historical scale is inferred; new inserts must record their own provenance.
ALTER TABLE brickvault.offers ALTER COLUMN price_scale_verified DROP DEFAULT;
UPDATE brickvault.offers o SET currency=p.currency FROM brickvault.purchases p WHERE p.id=o.purchase_id;
ALTER TABLE brickvault.offers
    ALTER COLUMN currency SET NOT NULL,
    ADD CONSTRAINT offers_currency_check CHECK (currency COLLATE "C" ~ '^[A-Z]{3}$');
