-- Fix product negative keyword normalization so punctuation is preserved.
-- This allows values like "2" and "(2)" to coexist as distinct keywords.

DO $$
BEGIN
    IF to_regclass('public.product_negative_keyword') IS NULL THEN
        RAISE NOTICE 'product_negative_keyword table not found; skipping migration.';
        RETURN;
    END IF;

    -- Rebuild normalized column + uniqueness to use lower(trim(keyword))
    -- instead of stripping punctuation.
    DROP INDEX IF EXISTS ux_product_negative_keyword_product_norm;

    ALTER TABLE product_negative_keyword
        DROP COLUMN IF EXISTS normalized_keyword;

    ALTER TABLE product_negative_keyword
        ADD COLUMN normalized_keyword text
        GENERATED ALWAYS AS (lower(btrim(keyword))) STORED;

    CREATE UNIQUE INDEX IF NOT EXISTS ux_product_negative_keyword_product_norm
        ON product_negative_keyword (product_id, normalized_keyword);
END $$;
