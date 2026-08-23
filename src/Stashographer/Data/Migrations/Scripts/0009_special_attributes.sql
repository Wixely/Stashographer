-- Typed, system-recognized attributes are separate from the ordinary string attribute bag.
-- JSON keeps the storage extensible; the expression index makes the first special attribute,
-- price, efficient to organize and report on without parsing a display string.

ALTER TABLE Items ADD COLUMN SpecialAttributesJson TEXT NOT NULL DEFAULT '{}';

CREATE INDEX IX_Items_SpecialPrice
ON Items (
    json_extract(SpecialAttributesJson, '$.price.currencyCode'),
    CAST(json_extract(SpecialAttributesJson, '$.price.decimalValue') AS NUMERIC)
);
