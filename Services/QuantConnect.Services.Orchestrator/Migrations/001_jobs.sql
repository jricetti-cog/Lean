-- job-orchestrator schema v1 (plan step s4.1). The orchestrator is the only writer of this database (ADR 0003).
CREATE TABLE IF NOT EXISTS jobs (
    id                  uuid PRIMARY KEY,
    kind                text        NOT NULL,
    status              text        NOT NULL,
    attempt             integer     NOT NULL DEFAULT 1,
    parent_job_id       uuid        NULL REFERENCES jobs (id),
    name                text        NULL,
    submitted_by        text        NOT NULL,
    created_at          timestamptz NOT NULL,
    started_at          timestamptz NULL,
    finished_at         timestamptz NULL,
    deadline            timestamptz NULL,
    progress            double precision NULL,
    lease_epoch         bigint      NULL,
    worker_id           text        NULL,
    attempt_taken_at    timestamptz NULL,
    last_heartbeat_at   timestamptz NULL,
    cancel_requested_at timestamptz NULL,
    cancel_reason       text        NULL,
    error               jsonb       NULL,
    result              jsonb       NULL,
    inputs              jsonb       NULL,
    spec                jsonb       NOT NULL,
    idempotency_key     text        NULL UNIQUE,
    request_hash        text        NULL,
    updated_at          timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS jobs_status_idx ON jobs (status);
CREATE INDEX IF NOT EXISTS jobs_parent_idx ON jobs (parent_job_id) WHERE parent_job_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS jobs_created_idx ON jobs (created_at DESC, id DESC);

-- Every publish goes through the outbox, written in the same transaction as the state change it announces.
CREATE TABLE IF NOT EXISTS outbox (
    id               bigserial PRIMARY KEY,
    job_id           uuid        NOT NULL REFERENCES jobs (id),
    exchange         text        NOT NULL,
    routing_key      text        NOT NULL,
    envelope         jsonb       NOT NULL,
    body             bytea       NOT NULL,
    mandatory        boolean     NOT NULL,
    created_at       timestamptz NOT NULL,
    sent_at          timestamptz NULL,
    publish_attempts integer     NOT NULL DEFAULT 0,
    last_error       text        NULL
);
CREATE INDEX IF NOT EXISTS outbox_unsent_idx ON outbox (id) WHERE sent_at IS NULL;

-- Processed lean.events deliveries, keyed by message id so redeliveries are no-ops.
CREATE TABLE IF NOT EXISTS job_events (
    message_id  uuid PRIMARY KEY,
    job_id      uuid        NOT NULL,
    attempt     integer     NOT NULL,
    type        text        NOT NULL,
    received_at timestamptz NOT NULL,
    applied     boolean     NOT NULL
);
CREATE INDEX IF NOT EXISTS job_events_job_idx ON job_events (job_id, received_at);
