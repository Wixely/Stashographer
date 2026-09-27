-- Keep the untouched intake capture after fan-out and identify the review rows generated
-- from one capture. This lets a multi-item image be detected again without narrowing future
-- attempts to the first crop or accumulating duplicate child rows.

ALTER TABLE IntakeQueueItems ADD COLUMN OriginalImageId INTEGER NULL
    REFERENCES Images(Id) ON DELETE SET NULL;
ALTER TABLE IntakeQueueItems ADD COLUMN CaptureGroupId INTEGER NULL
    REFERENCES IntakeQueueItems(Id) ON DELETE SET NULL;

-- Existing focused crops retain their parent in ImageDerivations. Captures that were never
-- cropped simply use their current image as the original.
UPDATE IntakeQueueItems AS q
SET OriginalImageId = COALESCE(
    (SELECT d.ParentImageId
     FROM ImageDerivations d
     WHERE d.ChildImageId = q.ImageId AND d.Kind = 0
     ORDER BY d.CreatedAt
     LIMIT 1),
    q.ImageId)
WHERE q.SourceType IN (1, 3) AND q.ImageId IS NOT NULL;

-- Historical fan-out rows share the source session and exact capture timestamp. Group them
-- by the recovered original as well, so two independent captures are not combined merely
-- because image content de-duplicated to the same Images row.
UPDATE IntakeQueueItems AS q
SET CaptureGroupId = (
    SELECT MIN(candidate.Id)
    FROM IntakeQueueItems candidate
    WHERE candidate.SessionId = q.SessionId
      AND candidate.CreatedAt = q.CreatedAt
      AND candidate.SourceType = 1
      AND COALESCE(
          (SELECT d.ParentImageId
           FROM ImageDerivations d
           WHERE d.ChildImageId = candidate.ImageId AND d.Kind = 0
           ORDER BY d.CreatedAt
           LIMIT 1),
          candidate.ImageId) = q.OriginalImageId)
WHERE q.SourceType = 1 AND q.OriginalImageId IS NOT NULL;

-- A historical group with several generated rows is known to have been a multi-item capture.
UPDATE IntakeQueueItems AS q
SET IsMultiPhoto = 1
WHERE q.Id = q.CaptureGroupId
  AND (SELECT COUNT(*) FROM IntakeQueueItems member
       WHERE member.CaptureGroupId = q.CaptureGroupId) > 1;

CREATE INDEX IX_IntakeQueueItems_CaptureGroup
    ON IntakeQueueItems(CaptureGroupId, Status, Id);
CREATE INDEX IX_IntakeQueueItems_OriginalImage
    ON IntakeQueueItems(OriginalImageId);
