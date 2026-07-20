-- Image storage: originals live on disk (see ImageService); this table holds only metadata.
-- Entities reference their primary image by ImageId. Columns are added without an inline
-- FOREIGN KEY clause because SQLite's ALTER TABLE ADD COLUMN cannot attach one; referential
-- cleanup is handled in ImageService on delete.

CREATE TABLE Images (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    StorageKey   TEXT    NOT NULL,          -- filename on disk (guid + extension)
    ContentType  TEXT    NOT NULL,
    OriginalName TEXT    NULL,
    Width        INTEGER NULL,
    Height       INTEGER NULL,
    ByteSize     INTEGER NULL,
    Sha256       TEXT    NULL,              -- content hash for de-duplication
    SourceUrl    TEXT    NULL,              -- set when downloaded from a URL
    CreatedAt    TEXT    NOT NULL
);

CREATE INDEX IX_Images_Sha256 ON Images(Sha256);

ALTER TABLE Items      ADD COLUMN ImageId INTEGER NULL;
ALTER TABLE Containers ADD COLUMN ImageId INTEGER NULL;
ALTER TABLE Locations  ADD COLUMN ImageId INTEGER NULL;
