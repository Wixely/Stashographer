-- Linked item entries let a quantity be split across independently managed places while
-- retaining the fact that the rows originated as one logical collection.
ALTER TABLE Items ADD COLUMN CollectionKey TEXT NULL;

CREATE INDEX IX_Items_CollectionKey ON Items(CollectionKey);
