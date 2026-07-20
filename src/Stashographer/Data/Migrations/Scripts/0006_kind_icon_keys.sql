-- Normalize ItemKinds.Icon to short IconCatalog keys ('Kitchen', 'MenuBook', ...).
-- The 0001 seed stored C# identifier paths ('Icons.Material.Filled.Kitchen') which nothing
-- can resolve at runtime; IconCatalog.Resolve works off the short key.

UPDATE ItemKinds SET Icon = REPLACE(Icon, 'Icons.Material.Filled.', '') WHERE Icon LIKE 'Icons.%';
