-- Migration: add scrape_enabled and scrape_config to store table
-- Idempotent alterations
ALTER TABLE store ADD COLUMN IF NOT EXISTS scrape_enabled boolean DEFAULT false;
ALTER TABLE store ADD COLUMN IF NOT EXISTS scrape_config jsonb;
-- Optional: index to quickly filter enabled scraping profiles
CREATE INDEX IF NOT EXISTS idx_store_scrape_enabled ON store (scrape_enabled);
-- Optional: GIN index if querying inside scrape_config JSON structure
CREATE INDEX IF NOT EXISTS idx_store_scrape_config_gin ON store USING GIN (scrape_config);
