-- Keep live barcode captures durable while allowing a consecutive identical read to
-- update their total quantity before background processing or automatic acceptance.
ALTER TABLE IntakeQueueItems ADD COLUMN CaptureQuantity INTEGER NOT NULL DEFAULT 1;
ALTER TABLE IntakeQueueItems ADD COLUMN LiveCaptureHoldUntil TEXT NULL;
