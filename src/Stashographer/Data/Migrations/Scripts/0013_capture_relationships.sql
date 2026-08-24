-- Preserve the agent's instance-level comparison separately from ordinary product matching.
-- A matched queue item points at the earlier capture believed to show the same object.

ALTER TABLE IntakeQueueItems
    ADD COLUMN MatchedQueueItemId INTEGER NULL REFERENCES IntakeQueueItems(Id) ON DELETE SET NULL;
ALTER TABLE IntakeQueueItems
    ADD COLUMN CaptureRelationship INTEGER NULL;
ALTER TABLE IntakeQueueItems
    ADD COLUMN RelationshipConfidence INTEGER NULL;
ALTER TABLE IntakeQueueItems
    ADD COLUMN RelationshipReason TEXT NULL;
ALTER TABLE IntakeQueueItems
    ADD COLUMN SuggestedImageRole INTEGER NULL;

CREATE INDEX IX_IntakeQueueItems_MatchedQueueItemId
    ON IntakeQueueItems(MatchedQueueItemId);
