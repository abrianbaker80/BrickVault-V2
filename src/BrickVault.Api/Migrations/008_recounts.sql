ALTER TABLE brickvault.commands DROP CONSTRAINT commands_kind_check;
ALTER TABLE brickvault.commands ADD CONSTRAINT commands_kind_check
    CHECK (kind IN ('catalog','location','purchase','delivery','putaway','offer','transfer','count','hold','hold-release','stock-loss','recount'));

CREATE TABLE brickvault.recount_previews (
    id uuid PRIMARY KEY,
    mode text NOT NULL CHECK (mode IN ('selected','matching')),
    query text,
    item_type text,
    location_id uuid,
    digest bytea NOT NULL CHECK (octet_length(digest)=32),
    frozen_at timestamptz NOT NULL DEFAULT statement_timestamp()
);
CREATE TABLE brickvault.recount_preview_targets (
    preview_id uuid NOT NULL REFERENCES brickvault.recount_previews(id),
    position_id uuid NOT NULL,
    purchase_id uuid,
    catalog_id uuid,
    location_id uuid,
    purchase_reference text,
    delivery_reference text,
    item_name text,
    location_code text,
    purchase_revision integer,
    catalog_revision integer,
    location_revision integer,
    quantity integer,
    PRIMARY KEY (preview_id,position_id),
    FOREIGN KEY (purchase_id,position_id) REFERENCES brickvault.placement_positions(purchase_id,id),
    CHECK (
        (num_nonnulls(purchase_id,catalog_id,location_id,purchase_reference,delivery_reference,item_name,location_code,
            purchase_revision,catalog_revision,location_revision,quantity)=0)
        OR (num_nulls(purchase_id,catalog_id,location_id,purchase_reference,delivery_reference,item_name,location_code,
            purchase_revision,catalog_revision,location_revision,quantity)=0
            AND purchase_revision>0 AND catalog_revision>0 AND location_revision>0 AND quantity>=0)
    )
);
CREATE TABLE brickvault.recount_batches (
    id uuid PRIMARY KEY REFERENCES brickvault.recount_previews(id),
    command_id uuid NOT NULL UNIQUE REFERENCES brickvault.audit(command_id) DEFERRABLE INITIALLY DEFERRED,
    queued_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX recount_batches_recent ON brickvault.recount_batches(queued_at DESC,id DESC);
CREATE TABLE brickvault.recount_results (
    batch_id uuid NOT NULL REFERENCES brickvault.recount_batches(id),
    position_id uuid NOT NULL,
    outcome text NOT NULL CHECK (outcome IN ('queued','changed','already-queued','unavailable')),
    queue_batch_id uuid,
    PRIMARY KEY (batch_id,position_id),
    FOREIGN KEY (batch_id,position_id) REFERENCES brickvault.recount_preview_targets(preview_id,position_id),
    FOREIGN KEY (queue_batch_id,position_id) REFERENCES brickvault.recount_results(batch_id,position_id),
    CHECK (CASE outcome WHEN 'queued' THEN queue_batch_id IS NOT NULL AND queue_batch_id=batch_id
        WHEN 'already-queued' THEN queue_batch_id IS NOT NULL AND queue_batch_id<>batch_id
        ELSE queue_batch_id IS NULL END)
);
CREATE INDEX recount_results_queued_position ON brickvault.recount_results(position_id,batch_id) WHERE outcome='queued';
-- A witness advances the purchase revision under the same purchase row lock used to queue work.
-- The first qualifying witness settles the task, even when its observed timestamp predates another transaction's commit.
CREATE VIEW brickvault.recount_tasks AS
SELECT r.batch_id,r.position_id,t.purchase_revision,w.id AS observation_id
FROM brickvault.recount_results r
JOIN brickvault.recount_preview_targets t ON t.preview_id=r.batch_id AND t.position_id=r.position_id
LEFT JOIN LATERAL (
    SELECT n.id FROM brickvault.stock_count_observations n
    WHERE n.position_id=r.position_id AND n.expected_purchase_revision>=t.purchase_revision
    ORDER BY n.expected_purchase_revision,n.id LIMIT 1
) w ON true
WHERE r.outcome='queued';
