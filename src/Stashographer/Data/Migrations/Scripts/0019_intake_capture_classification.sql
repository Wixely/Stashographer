-- Remember explicit source-type choices so a manual correction is not undone by AI.
ALTER TABLE IntakeQueueItems ADD COLUMN SourceTypeOverride INTEGER NOT NULL DEFAULT 0;
