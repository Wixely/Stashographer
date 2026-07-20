-- Inventory view mode per quick link: 0 = List (data table), 1 = Gallery (cover grid).
-- The seeded Books link opens in Gallery — portrait covers suit books.

ALTER TABLE QuickLinks ADD COLUMN ViewMode INTEGER NOT NULL DEFAULT 0;

UPDATE QuickLinks SET ViewMode = 1 WHERE Label = 'Books';
