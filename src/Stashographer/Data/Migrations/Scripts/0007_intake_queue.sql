-- Durable, session-scoped intake queue. Captures are persisted before any lookup or AI
-- work so rapid barcode/photo intake is never coupled to processing latency.

CREATE TABLE IntakeSessions (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    StartedAt          TEXT    NOT NULL,
    EndedAt            TEXT    NULL
);

CREATE TABLE IntakeQueueItems (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId          INTEGER NOT NULL REFERENCES IntakeSessions(Id),
    SourceType         INTEGER NOT NULL,
    SourceCode         TEXT    NULL,
    ImageId            INTEGER NULL,
    IsMultiPhoto       INTEGER NOT NULL DEFAULT 0,
    Status             INTEGER NOT NULL DEFAULT 0,
    DraftJson          TEXT    NULL,
    ProposalAction     INTEGER NULL,
    MatchedItemId      INTEGER NULL REFERENCES Items(Id) ON DELETE SET NULL,
    MatchedItemName    TEXT    NULL,
    IncrementBy        NUMERIC NOT NULL DEFAULT 1,
    AppliedItemId      INTEGER NULL REFERENCES Items(Id) ON DELETE SET NULL,
    Error              TEXT    NULL,
    CreatedAt          TEXT    NOT NULL,
    ProcessingStartedAt TEXT   NULL,
    ProcessedAt        TEXT    NULL,
    ReviewedAt         TEXT    NULL
);

CREATE INDEX IX_IntakeQueueItems_Status ON IntakeQueueItems(Status, Id);
CREATE INDEX IX_IntakeQueueItems_Session ON IntakeQueueItems(SessionId, Id);
