-- Make attribute enum synonyms attribute-level instead of product-specific.
-- Synonyms apply to the attribute_enum_value regardless of which product uses it.

-- 1. Drop the old unique constraint and indexes that reference product_id.
DROP INDEX IF EXISTS ux_product_attribute_enum_synonym_product_enum_norm;
DROP INDEX IF EXISTS ix_product_attribute_enum_synonym_product_enum_active;
DROP INDEX IF EXISTS ix_product_attribute_enum_synonym_product_attr;

-- 2. Before dropping the column, collapse any duplicate synonyms that only
--    differed by product_id.  Keep the earliest row per (enum_value_id, normalized_synonym).
DELETE FROM product_attribute_enum_synonym a
USING product_attribute_enum_synonym b
WHERE a.enum_value_id = b.enum_value_id
  AND a.normalized_synonym = b.normalized_synonym
  AND a.id > b.id;

-- 3. Drop the product_id column.
ALTER TABLE product_attribute_enum_synonym DROP COLUMN product_id;

-- 4. Re-create indexes without product_id.
CREATE UNIQUE INDEX ux_product_attribute_enum_synonym_enum_norm
  ON product_attribute_enum_synonym(enum_value_id, normalized_synonym);

CREATE INDEX ix_product_attribute_enum_synonym_enum_active
  ON product_attribute_enum_synonym(enum_value_id, is_active);

CREATE INDEX ix_product_attribute_enum_synonym_attr
  ON product_attribute_enum_synonym(attribute_id);
