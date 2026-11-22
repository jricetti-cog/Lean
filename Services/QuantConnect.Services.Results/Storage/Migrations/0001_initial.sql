-- Results database (ADR 0003): one row per job attempt plus its orders, order events, logs and chart index.
CREATE TABLE IF NOT EXISTS results (
    job_id                  uuid             NOT NULL,
    attempt                 integer          NOT NULL,
    kind                    text             NOT NULL,
    parent_job_id           uuid,
    status                  text             NOT NULL,
    algorithm_id            text,
    progress                double precision NOT NULL DEFAULT 0,
    parameters              jsonb,
    statistics              jsonb            NOT NULL DEFAULT '{}'::jsonb,
    runtime_statistics      jsonb            NOT NULL DEFAULT '{}'::jsonb,
    algorithm_configuration jsonb,
    state                   jsonb,
    order_list_hash         text,
    order_count             bigint           NOT NULL DEFAULT 0,
    result_ref              jsonb,
    results_prefix          jsonb,
    artifacts               jsonb            NOT NULL DEFAULT '[]'::jsonb,
    error                   jsonb,
    last_sequence           bigint           NOT NULL DEFAULT 0,
    final_indexed           boolean          NOT NULL DEFAULT false,
    created_at              timestamptz      NOT NULL DEFAULT now(),
    updated_at              timestamptz      NOT NULL DEFAULT now(),
    PRIMARY KEY (job_id, attempt)
);
CREATE INDEX IF NOT EXISTS results_parent_job_id ON results (parent_job_id) WHERE parent_job_id IS NOT NULL;

-- Idempotency inbox: one row per applied message, keyed by packet sequence number ("seq:<n>") or,
-- for messages without one (job.* status events), by message id ("msg:<uuid>").
CREATE TABLE IF NOT EXISTS processed_messages (
    job_id       uuid        NOT NULL,
    attempt      integer     NOT NULL,
    dedup_key    text        NOT NULL,
    message_type text        NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (job_id, attempt, dedup_key)
);

CREATE TABLE IF NOT EXISTS orders (
    job_id     uuid        NOT NULL,
    attempt    integer     NOT NULL,
    order_id   integer     NOT NULL,
    sequence   bigint      NOT NULL,
    body       jsonb       NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (job_id, attempt, order_id),
    FOREIGN KEY (job_id, attempt) REFERENCES results (job_id, attempt) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS order_events (
    job_id   uuid        NOT NULL,
    attempt  integer     NOT NULL,
    order_id integer     NOT NULL,
    event_id integer     NOT NULL,
    utc_time timestamptz NOT NULL,
    body     jsonb       NOT NULL,
    PRIMARY KEY (job_id, attempt, order_id, event_id),
    FOREIGN KEY (job_id, attempt) REFERENCES results (job_id, attempt) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS order_events_by_time ON order_events (job_id, attempt, utc_time, order_id, event_id);

CREATE TABLE IF NOT EXISTS logs (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id      uuid        NOT NULL,
    attempt     integer     NOT NULL,
    sequence    bigint,
    type        text        NOT NULL,
    message     text        NOT NULL,
    stack_trace text,
    time        timestamptz NOT NULL,
    FOREIGN KEY (job_id, attempt) REFERENCES results (job_id, attempt) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS logs_by_attempt ON logs (job_id, attempt, id);

-- Chart index with downsampled series (the full-resolution chart stays in the result JSON in MinIO).
CREATE TABLE IF NOT EXISTS charts (
    job_id      uuid        NOT NULL,
    attempt     integer     NOT NULL,
    name        text        NOT NULL,
    body        jsonb       NOT NULL,
    point_count integer     NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (job_id, attempt, name),
    FOREIGN KEY (job_id, attempt) REFERENCES results (job_id, attempt) ON DELETE CASCADE
);
