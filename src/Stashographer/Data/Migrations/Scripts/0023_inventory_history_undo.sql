-- Keep removed inventory records for history and record enough intake effects to undo them.

ALTER TABLE Items ADD COLUMN IsInStock INTEGER NOT NULL DEFAULT 1;
CREATE INDEX IX_Items_IsInStock ON Items(IsInStock, Name);

ALTER TABLE IntakeQueueItems ADD COLUMN AppliedAction INTEGER NULL;
ALTER TABLE IntakeQueueItems ADD COLUMN AppliedQuantity NUMERIC NULL;
ALTER TABLE IntakeQueueItems ADD COLUMN AppliedImageId INTEGER NULL REFERENCES Images(Id) ON DELETE SET NULL;
ALTER TABLE IntakeQueueItems ADD COLUMN UndoneAt TEXT NULL;
