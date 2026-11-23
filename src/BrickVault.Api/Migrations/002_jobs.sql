CREATE TABLE brickvault.jobs (
    command_id uuid PRIMARY KEY REFERENCES brickvault.commands(command_id),
    payload_version integer NOT NULL CHECK (payload_version > 0),
    kind text NOT NULL,
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','done','blocked')),
    enqueued_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX jobs_ready ON brickvault.jobs(enqueued_at, command_id) WHERE status = 'pending';
CREATE TABLE brickvault.job_effects (
    command_id uuid PRIMARY KEY REFERENCES brickvault.jobs(command_id),
    processed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE brickvault.worker_health (
    name text PRIMARY KEY CHECK (name = 'reference-worker'),
    heartbeat_at timestamptz NOT NULL
);
