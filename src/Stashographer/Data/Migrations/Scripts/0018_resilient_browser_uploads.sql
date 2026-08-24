-- Browser-selected files must not depend on a live Blazor Server circuit. The browser
-- assigns every direct HTTP upload an idempotency token so a reconnect/retry cannot add
-- the same capture to the durable intake queue twice.
ALTER TABLE IntakeQueueItems ADD COLUMN BrowserUploadToken TEXT NULL;

CREATE UNIQUE INDEX IX_IntakeQueueItems_BrowserUploadToken
    ON IntakeQueueItems(BrowserUploadToken)
    WHERE BrowserUploadToken IS NOT NULL;

CREATE TABLE BrowserUploads (
    Token       TEXT PRIMARY KEY,
    Kind        INTEGER NOT NULL,
    Status      INTEGER NOT NULL,
    ImageId     INTEGER NULL REFERENCES Images(Id) ON DELETE SET NULL,
    QueueItemId INTEGER NULL REFERENCES IntakeQueueItems(Id) ON DELETE SET NULL,
    Code        TEXT NULL,
    CreatedAt   TEXT NOT NULL,
    CompletedAt TEXT NULL
);

CREATE INDEX IX_BrowserUploads_CreatedAt ON BrowserUploads(CreatedAt);
