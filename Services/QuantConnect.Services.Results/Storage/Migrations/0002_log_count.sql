-- Log lines held per attempt, kept next to the attempt's row (locked by every ingest transaction) so the
-- Results:MaxLogsPerAttempt cap only deletes when it is exceeded instead of scanning the newest lines on every insert.
ALTER TABLE results ADD COLUMN IF NOT EXISTS log_count bigint NOT NULL DEFAULT 0;
UPDATE results SET log_count = counts.n
FROM (SELECT job_id, attempt, count(*) AS n FROM logs GROUP BY job_id, attempt) AS counts
WHERE results.job_id = counts.job_id AND results.attempt = counts.attempt;
