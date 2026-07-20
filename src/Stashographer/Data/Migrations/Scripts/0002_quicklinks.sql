-- Configurable home-screen quick links (large launcher tiles).
-- Target: 0 = Dashboard, 1 = Scan, 2 = Inventory (with optional kind filters).
-- IncludeKindIds / ExcludeKindIds are JSON arrays of ItemKind ids (1=Grocery, 2=Book, ...).

CREATE TABLE QuickLinks (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    Label          TEXT    NOT NULL,
    Icon           TEXT    NULL,
    Target         INTEGER NOT NULL DEFAULT 2,
    IncludeKindIds TEXT    NOT NULL DEFAULT '[]',
    ExcludeKindIds TEXT    NOT NULL DEFAULT '[]',
    SortOrder      INTEGER NOT NULL DEFAULT 0
);

INSERT INTO QuickLinks (Label, Icon, Target, IncludeKindIds, ExcludeKindIds, SortOrder) VALUES
    ('Items',     'Inventory2',     2, '[]',  '[1,2]', 1),  -- everything except groceries & books
    ('Groceries', 'Kitchen',        2, '[1]', '[]',    2),
    ('Books',     'MenuBook',       2, '[2]', '[]',    3),
    ('Dashboard', 'Dashboard',      0, '[]',  '[]',    4),
    ('Scan',      'QrCodeScanner',  1, '[]',  '[]',    5);
