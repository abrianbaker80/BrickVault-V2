-- Before independent pricing, all selling prices were entered in the purchase currency.
ALTER TABLE brickvault.offers ADD COLUMN currency varchar(3);
UPDATE brickvault.offers o SET currency=p.currency FROM brickvault.purchases p WHERE p.id=o.purchase_id;
ALTER TABLE brickvault.offers
    ALTER COLUMN currency SET NOT NULL,
    ADD CONSTRAINT offers_currency_check CHECK (currency COLLATE "C" ~ '^[A-Z]{3}$');
