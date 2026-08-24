-- Reviewable meal plans only change stock when an entry is explicitly marked cooked.
-- Consumption events retain exact lot-level lines for history and undo.

CREATE TABLE MealPlans (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Name      TEXT    NOT NULL,
    StartDate TEXT    NOT NULL,
    EndDate   TEXT    NOT NULL,
    Notes     TEXT    NULL,
    CreatedAt TEXT    NOT NULL,
    UpdatedAt TEXT    NOT NULL
);

CREATE TABLE MealPlanEntries (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    MealPlanId      INTEGER NOT NULL REFERENCES MealPlans(Id) ON DELETE CASCADE,
    PlanDate        TEXT    NOT NULL,
    MealSlot        TEXT    NOT NULL,
    BomDefinitionId INTEGER NULL REFERENCES BomDefinitions(Id) ON DELETE SET NULL,
    RecipeName      TEXT    NOT NULL,
    OutputQuantity  NUMERIC NOT NULL,
    OutputUnit      TEXT    NULL,
    Reason          TEXT    NULL,
    Status          INTEGER NOT NULL DEFAULT 0,
    CookedAt        TEXT    NULL
);

CREATE INDEX IX_MealPlanEntries_PlanDate ON MealPlanEntries(PlanDate, MealSlot, Id);
CREATE INDEX IX_MealPlanEntries_MealPlanId ON MealPlanEntries(MealPlanId, PlanDate, Id);

CREATE TABLE ConsumptionEvents (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    MealPlanEntryId INTEGER NULL REFERENCES MealPlanEntries(Id) ON DELETE SET NULL,
    BomDefinitionId INTEGER NULL REFERENCES BomDefinitions(Id) ON DELETE SET NULL,
    Description     TEXT    NOT NULL,
    ConsumedAt      TEXT    NOT NULL,
    UndoneAt        TEXT    NULL
);

CREATE UNIQUE INDEX IX_ConsumptionEvents_ActiveMealEntry
    ON ConsumptionEvents(MealPlanEntryId) WHERE MealPlanEntryId IS NOT NULL AND UndoneAt IS NULL;
CREATE INDEX IX_ConsumptionEvents_ConsumedAt ON ConsumptionEvents(ConsumedAt DESC);

CREATE TABLE ConsumptionLines (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    ConsumptionEventId INTEGER NOT NULL REFERENCES ConsumptionEvents(Id) ON DELETE CASCADE,
    ItemId             INTEGER NULL REFERENCES Items(Id) ON DELETE SET NULL,
    ItemName           TEXT    NOT NULL,
    Quantity           NUMERIC NOT NULL,
    Unit               TEXT    NULL,
    ExpiryDate         TEXT    NULL
);

CREATE INDEX IX_ConsumptionLines_EventId ON ConsumptionLines(ConsumptionEventId, Id);
CREATE INDEX IX_ConsumptionLines_ItemId ON ConsumptionLines(ItemId, ConsumptionEventId);
