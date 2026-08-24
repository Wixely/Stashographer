-- Generic bills of materials support both food recipes and non-food builds. Requirements
-- can match inventory semantically or use an explicit allow-list of interchangeable items.

CREATE TABLE BomDefinitions (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    Name           TEXT    NOT NULL,
    Kind           INTEGER NOT NULL DEFAULT 0,
    Description    TEXT    NULL,
    OutputQuantity NUMERIC NOT NULL DEFAULT 1,
    OutputUnit     TEXT    NULL,
    CreatedAt      TEXT    NOT NULL,
    UpdatedAt      TEXT    NOT NULL
);

CREATE TABLE BomRequirements (
    Id                     INTEGER PRIMARY KEY AUTOINCREMENT,
    BomDefinitionId        INTEGER NOT NULL REFERENCES BomDefinitions(Id) ON DELETE CASCADE,
    Name                   TEXT    NOT NULL,
    Quantity               NUMERIC NOT NULL DEFAULT 1,
    Unit                   TEXT    NULL,
    IsOptional             INTEGER NOT NULL DEFAULT 0,
    MatchMode              INTEGER NOT NULL DEFAULT 0,
    MatchItemKindId        INTEGER NULL REFERENCES ItemKinds(Id) ON DELETE SET NULL,
    MatchText              TEXT    NULL,
    RequiredAttributesJson TEXT    NOT NULL DEFAULT '{}',
    SortOrder              INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE BomRequirementCandidates (
    RequirementId INTEGER NOT NULL REFERENCES BomRequirements(Id) ON DELETE CASCADE,
    ItemId        INTEGER NOT NULL REFERENCES Items(Id) ON DELETE CASCADE,
    PRIMARY KEY (RequirementId, ItemId)
);

CREATE INDEX IX_BomRequirements_Definition
ON BomRequirements(BomDefinitionId, SortOrder, Id);

CREATE INDEX IX_BomRequirementCandidates_Item
ON BomRequirementCandidates(ItemId, RequirementId);
