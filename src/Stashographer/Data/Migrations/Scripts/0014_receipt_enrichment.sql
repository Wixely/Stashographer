-- A receipt is a reviewable enrichment source, not a separately counted inventory item.
-- Its image can be shared by several stock lots and every accepted link retains the
-- originating queue item and receipt line for provenance and idempotency.

ALTER TABLE IntakeQueueItems ADD COLUMN ReceiptJson TEXT NULL;

CREATE TABLE ItemPurchases (
    Id               INTEGER PRIMARY KEY AUTOINCREMENT,
    QueueItemId      INTEGER NOT NULL REFERENCES IntakeQueueItems(Id) ON DELETE CASCADE,
    ReceiptLineIndex INTEGER NOT NULL,
    ItemId           INTEGER NOT NULL REFERENCES Items(Id) ON DELETE CASCADE,
    ImageId          INTEGER NOT NULL REFERENCES Images(Id) ON DELETE CASCADE,
    Merchant         TEXT    NULL,
    PurchasedOn      TEXT    NULL,
    Description      TEXT    NOT NULL,
    Quantity         NUMERIC NOT NULL DEFAULT 1,
    UnitPrice        NUMERIC NULL,
    Currency         TEXT    NULL,
    LineTotal        NUMERIC NULL,
    Confidence       INTEGER NULL,
    CreatedAt        TEXT    NOT NULL,
    UNIQUE (QueueItemId, ReceiptLineIndex, ItemId)
);

CREATE INDEX IX_ItemPurchases_ItemId ON ItemPurchases(ItemId, PurchasedOn);
CREATE INDEX IX_ItemPurchases_ImageId ON ItemPurchases(ImageId);
