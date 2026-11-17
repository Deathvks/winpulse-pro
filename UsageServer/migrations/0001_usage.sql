CREATE TABLE usage_batches (
    id TEXT PRIMARY KEY,
    received_day TEXT NOT NULL,
    version TEXT NOT NULL,
    counters TEXT NOT NULL CHECK (json_valid(counters))
);

CREATE INDEX usage_batches_day ON usage_batches (received_day, version);

CREATE TABLE telegram_reports (
    report_day TEXT PRIMARY KEY
);
