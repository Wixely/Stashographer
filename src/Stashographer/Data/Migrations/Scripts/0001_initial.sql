-- Stashographer initial schema (SQLite).

CREATE TABLE ItemKinds (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    Name                TEXT    NOT NULL UNIQUE,
    Icon                TEXT    NULL,
    SuggestedAttributes TEXT    NOT NULL DEFAULT '[]',
    IsSystem            INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE Locations (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT    NOT NULL,
    Description TEXT    NULL
);

CREATE TABLE Containers (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    Name          TEXT    NOT NULL,
    ContainerType INTEGER NOT NULL DEFAULT 0,
    QrSlug        TEXT    NOT NULL UNIQUE,
    Description   TEXT    NULL,
    LocationId    INTEGER NOT NULL REFERENCES Locations(Id) ON DELETE CASCADE
);

CREATE TABLE Items (
    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
    Code              TEXT    NULL,
    Name              TEXT    NOT NULL,
    Description       TEXT    NULL,
    ItemKindId        INTEGER NOT NULL REFERENCES ItemKinds(Id),
    Quantity          NUMERIC NOT NULL DEFAULT 1,
    Unit              TEXT    NULL,
    LowStockThreshold NUMERIC NOT NULL DEFAULT 0,
    ExpiryDate        TEXT    NULL,
    LocationId        INTEGER NULL REFERENCES Locations(Id) ON DELETE SET NULL,
    ContainerId       INTEGER NULL REFERENCES Containers(Id) ON DELETE SET NULL,
    ThumbnailUrl      TEXT    NULL,
    PhotoPath         TEXT    NULL,
    AttributesJson    TEXT    NOT NULL DEFAULT '{}',
    Notes             TEXT    NULL,
    CreatedAt         TEXT    NOT NULL,
    UpdatedAt         TEXT    NOT NULL
);

CREATE INDEX IX_Items_Code ON Items(Code);
CREATE INDEX IX_Items_ItemKindId ON Items(ItemKindId);
CREATE INDEX IX_Items_ContainerId ON Items(ContainerId);

CREATE TABLE Checkouts (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ItemId          INTEGER NOT NULL REFERENCES Items(Id) ON DELETE CASCADE,
    CheckedOutBy    TEXT    NOT NULL,
    WhereaboutsNote TEXT    NULL,
    CheckedOutAt    TEXT    NOT NULL,
    DueDate         TEXT    NULL,
    ReturnedAt      TEXT    NULL,
    Notes           TEXT    NULL
);

CREATE INDEX IX_Checkouts_Open ON Checkouts(ItemId) WHERE ReturnedAt IS NULL;

CREATE TABLE Tags (
    Id   INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT    NOT NULL UNIQUE
);

CREATE TABLE ItemTags (
    ItemId INTEGER NOT NULL REFERENCES Items(Id) ON DELETE CASCADE,
    TagId  INTEGER NOT NULL REFERENCES Tags(Id)  ON DELETE CASCADE,
    PRIMARY KEY (ItemId, TagId)
);

-- Seed: item kinds ----------------------------------------------------------
INSERT INTO ItemKinds (Id, Name, Icon, SuggestedAttributes, IsSystem) VALUES
    (1, 'Grocery',     'Icons.Material.Filled.Kitchen',   '["Brand","Category","Nutrition grade"]', 1),
    (2, 'Book',        'Icons.Material.Filled.MenuBook',  '["Author","Publisher","Published","Pages"]', 1),
    (3, 'Tool',        'Icons.Material.Filled.Handyman',  '["Brand","Model"]', 1),
    (4, 'Electronics', 'Icons.Material.Filled.Devices',   '["Brand","Model","Serial number"]', 1),
    (5, 'Media',       'Icons.Material.Filled.Album',     '["Format","Artist"]', 1),
    (6, 'Clothing',    'Icons.Material.Filled.Checkroom', '["Size","Colour"]', 1),
    (7, 'Other',       'Icons.Material.Filled.Category',  '[]', 1);

-- Seed: starter locations ---------------------------------------------------
INSERT INTO Locations (Id, Name, Description) VALUES
    (1, 'Kitchen',     'Pantry, fridge and cupboards'),
    (2, 'Living Room', NULL),
    (3, 'Garage',      NULL),
    (4, 'Loft',        NULL),
    (5, 'Study',       NULL);
