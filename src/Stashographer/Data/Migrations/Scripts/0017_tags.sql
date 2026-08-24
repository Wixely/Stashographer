-- Tags and ItemTags were reserved in the initial schema, but did not yet carry
-- lifecycle metadata or enforce the same case-insensitive identity used by the UI.
-- Rebuild both tables so existing databases keep their tag assignments.
CREATE TABLE Tags_new (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Name      TEXT NOT NULL COLLATE NOCASE UNIQUE,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

INSERT INTO Tags_new (Id, Name, CreatedAt, UpdatedAt)
SELECT MIN(Id), TRIM(Name),
       strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now'),
       strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now')
FROM Tags
GROUP BY LOWER(TRIM(Name));

CREATE TABLE ItemTags_new (
    ItemId INTEGER NOT NULL REFERENCES Items(Id) ON DELETE CASCADE,
    TagId  INTEGER NOT NULL REFERENCES Tags_new(Id) ON DELETE CASCADE,
    PRIMARY KEY (ItemId, TagId)
);

INSERT OR IGNORE INTO ItemTags_new (ItemId, TagId)
SELECT it.ItemId, replacement.Id
FROM ItemTags it
JOIN Tags original ON original.Id = it.TagId
JOIN Tags_new replacement ON replacement.Name = TRIM(original.Name) COLLATE NOCASE;

DROP TABLE ItemTags;
DROP TABLE Tags;
ALTER TABLE Tags_new RENAME TO Tags;
ALTER TABLE ItemTags_new RENAME TO ItemTags;

CREATE INDEX IX_ItemTags_TagId ON ItemTags(TagId, ItemId);
