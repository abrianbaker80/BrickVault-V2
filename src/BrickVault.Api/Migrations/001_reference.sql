CREATE TABLE brickvault.catalog (
    id uuid PRIMARY KEY,
    revision integer NOT NULL CHECK (revision > 0),
    item_type text NOT NULL CHECK (item_type IN ('part','set','minifigure','book','gear','instruction','original-box','catalog')),
    item_number varchar(64) NOT NULL CHECK (length(trim(item_number)) > 0),
    variant varchar(64),
    name varchar(160) NOT NULL CHECK (length(trim(name)) > 0),
    color_id integer CHECK (color_id >= 0),
    color_state text NOT NULL CHECK (color_state IN ('known','unknown','not-applicable')),
    condition text NOT NULL CHECK (condition IN ('new','used','unknown')),
    completeness text NOT NULL CHECK (completeness IN ('complete','incomplete','unknown','not-applicable')),
    source varchar(80) NOT NULL CHECK (length(trim(source)) > 0),
    license text NOT NULL CHECK (license IN ('operator-owned','permitted','unknown','restricted')),
    mapping text NOT NULL CHECK (mapping IN ('confirmed','ambiguous','unmapped')),
    CHECK ((color_id IS NOT NULL) = (color_state = 'known')),
    integration_eligible boolean GENERATED ALWAYS AS (
        mapping = 'confirmed' AND license IN ('operator-owned','permitted') AND color_state <> 'unknown'
        AND condition <> 'unknown' AND completeness <> 'unknown'
    ) STORED,
    UNIQUE NULLS NOT DISTINCT (source,item_type,item_number,variant,color_state,color_id,condition,completeness)
);
CREATE TABLE brickvault.locations (
    id uuid PRIMARY KEY,
    revision integer NOT NULL CHECK (revision > 0),
    code varchar(48) NOT NULL CHECK (code = upper(code) AND code ~ '^[A-Z0-9][A-Z0-9._-]*$') UNIQUE,
    name varchar(160) NOT NULL CHECK (length(trim(name)) > 0)
);
CREATE TABLE brickvault.commands (
    command_id uuid PRIMARY KEY,
    kind text NOT NULL CHECK (kind IN ('catalog','location')),
    digest bytea NOT NULL CHECK (octet_length(digest) = 32),
    entity_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    correlation_id uuid UNIQUE NOT NULL,
    UNIQUE (kind,entity_id,revision)
);
CREATE TABLE brickvault.audit (
    command_id uuid PRIMARY KEY REFERENCES brickvault.commands(command_id),
    actor text NOT NULL CHECK (actor = 'operator'),
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE brickvault.sessions (
    lookup bytea PRIMARY KEY CHECK (octet_length(lookup) = 32),
    expires_at timestamptz NOT NULL
);
CREATE INDEX sessions_expiry ON brickvault.sessions(expires_at);
