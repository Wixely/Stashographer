-- Classify the source of every consumption event. Existing events were all created by
-- the meal-plan workflow, so the migration default deliberately identifies them as meals.

ALTER TABLE ConsumptionEvents
    ADD COLUMN Kind INTEGER NOT NULL DEFAULT 1;

CREATE INDEX IX_ConsumptionEvents_KindConsumedAt
    ON ConsumptionEvents(Kind, ConsumedAt DESC);

CREATE INDEX IX_ConsumptionEvents_Active
    ON ConsumptionEvents(UndoneAt, ConsumedAt DESC);
