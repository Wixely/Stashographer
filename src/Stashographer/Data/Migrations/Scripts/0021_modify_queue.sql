-- Photo-first deferred modifications are deliberately separate from intake: every image is
-- retained as a reminder, AI may only suggest an existing item, and a person must choose the
-- action before inventory changes.

CREATE TABLE ModifySessions (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    StartedAt           TEXT    NOT NULL,
    EndedAt             TEXT    NULL,
    WorkingLocationId   INTEGER NULL REFERENCES Locations(Id) ON DELETE SET NULL,
    WorkingContainerId  INTEGER NULL REFERENCES Containers(Id) ON DELETE SET NULL
);

CREATE TABLE ModifyQueueItems (
    Id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId            INTEGER NOT NULL REFERENCES ModifySessions(Id),
    OriginalImageId      INTEGER NOT NULL REFERENCES Images(Id),
    ImageId              INTEGER NOT NULL REFERENCES Images(Id),
    IsMultiPhoto         INTEGER NOT NULL DEFAULT 1,
    BrowserUploadToken   TEXT    NULL,
    Status               INTEGER NOT NULL DEFAULT 0,
    IdentificationJson   TEXT    NULL,
    MatchedItemId        INTEGER NULL REFERENCES Items(Id) ON DELETE SET NULL,
    MatchedItemName      TEXT    NULL,
    MatchConfidence      INTEGER NOT NULL DEFAULT 0,
    MatchReason          TEXT    NULL,
    MatchedItemUpdatedAt TEXT    NULL,
    AppliedAction        INTEGER NULL,
    Error                TEXT    NULL,
    CreatedAt            TEXT    NOT NULL,
    ProcessingStartedAt  TEXT    NULL,
    ProcessedAt          TEXT    NULL,
    ReviewedAt           TEXT    NULL
);

CREATE UNIQUE INDEX IX_ModifyQueueItems_BrowserUploadToken
    ON ModifyQueueItems(BrowserUploadToken)
    WHERE BrowserUploadToken IS NOT NULL;
CREATE INDEX IX_ModifyQueueItems_Status ON ModifyQueueItems(Status, Id);
CREATE INDEX IX_ModifyQueueItems_Session ON ModifyQueueItems(SessionId, Id);

CREATE TABLE ModifyActionEvents (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    ModifyQueueItemId   INTEGER NOT NULL UNIQUE REFERENCES ModifyQueueItems(Id),
    ItemId              INTEGER NULL REFERENCES Items(Id) ON DELETE SET NULL,
    Action              INTEGER NOT NULL,
    BeforeJson          TEXT    NOT NULL,
    AfterJson           TEXT    NULL,
    ConsumptionEventId  INTEGER NULL REFERENCES ConsumptionEvents(Id) ON DELETE SET NULL,
    CreatedItemId       INTEGER NULL REFERENCES Items(Id) ON DELETE SET NULL,
    CreatedAt           TEXT    NOT NULL,
    AppliedAt           TEXT    NULL,
    Error               TEXT    NULL
);

ALTER TABLE BrowserUploads ADD COLUMN ModifyQueueItemId INTEGER NULL
    REFERENCES ModifyQueueItems(Id) ON DELETE SET NULL;

ALTER TABLE ConsumptionEvents ADD COLUMN ModifyQueueItemId INTEGER NULL
    REFERENCES ModifyQueueItems(Id) ON DELETE SET NULL;

CREATE UNIQUE INDEX IX_ConsumptionEvents_ModifyQueueItemId
    ON ConsumptionEvents(ModifyQueueItemId)
    WHERE ModifyQueueItemId IS NOT NULL;
