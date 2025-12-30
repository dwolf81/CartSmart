-- Idempotent migration: add store_item_id to deal_product and index
BEGIN;

-- Add column if missing
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'deal_product' AND column_name = 'store_item_id'
    ) THEN
        ALTER TABLE public.deal_product ADD COLUMN store_item_id text;
    END IF;
END$$;

-- Create index for fast dedupe (non-unique to allow nulls)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace 
        WHERE c.relname = 'idx_deal_product_store_item_id' AND n.nspname = 'public'
    ) THEN
        CREATE INDEX idx_deal_product_store_item_id ON public.deal_product (store_item_id);
    END IF;
END$$;

COMMIT;
