-- Multiple images can describe one inventory item, and one source image (for example a
-- receipt or group photo) can be associated with several items. Items.ImageId remains the
-- denormalized primary-image pointer for existing queries and UI compatibility.

CREATE TABLE ItemImages (
    ItemId     INTEGER NOT NULL REFERENCES Items(Id) ON DELETE CASCADE,
    ImageId    INTEGER NOT NULL REFERENCES Images(Id) ON DELETE CASCADE,
    Role       INTEGER NOT NULL DEFAULT 5,
    IsPrimary  INTEGER NOT NULL DEFAULT 0,
    SortOrder  INTEGER NOT NULL DEFAULT 0,
    CreatedAt  TEXT    NOT NULL,
    PRIMARY KEY (ItemId, ImageId)
);

CREATE UNIQUE INDEX IX_ItemImages_Primary
    ON ItemImages(ItemId) WHERE IsPrimary = 1;
CREATE INDEX IX_ItemImages_ImageId ON ItemImages(ImageId);

INSERT INTO ItemImages (ItemId, ImageId, Role, IsPrimary, SortOrder, CreatedAt)
SELECT Id, ImageId, 5, 1, 0, UpdatedAt
FROM Items
WHERE ImageId IS NOT NULL;

-- Derivations are separate from Images because content de-duplication means one stored image
-- can be produced from more than one source. Coordinates describe the actual retained region
-- after aspect-ratio expansion, padding, and bounds clamping.
CREATE TABLE ImageDerivations (
    ParentImageId INTEGER NOT NULL REFERENCES Images(Id) ON DELETE CASCADE,
    ChildImageId  INTEGER NOT NULL REFERENCES Images(Id) ON DELETE CASCADE,
    Kind          INTEGER NOT NULL DEFAULT 0,
    CropX         NUMERIC NULL,
    CropY         NUMERIC NULL,
    CropWidth     NUMERIC NULL,
    CropHeight    NUMERIC NULL,
    CreatedAt     TEXT    NOT NULL,
    PRIMARY KEY (ParentImageId, ChildImageId, Kind)
);

CREATE INDEX IX_ImageDerivations_ChildImageId ON ImageDerivations(ChildImageId);
