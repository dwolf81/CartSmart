-- ============================================================================
-- Migration: scrape_log table + per-method enable flags on store
-- Date: 2026-03-14
-- ============================================================================

-- 1. Add per-method enable flags to the store table.
--    When scrape_mode_id = 1 (All), these control which server-side methods run.
--    Both default to TRUE so existing behaviour is unchanged.
ALTER TABLE store
  ADD COLUMN IF NOT EXISTS scrape_http_enabled  boolean NOT NULL DEFAULT true,
  ADD COLUMN IF NOT EXISTS scrape_playwright_enabled boolean NOT NULL DEFAULT true;

-- 2. Create scrape_log table to record every scrape attempt outcome.
CREATE TABLE IF NOT EXISTS scrape_log (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  store_id        integer NOT NULL REFERENCES store(id),
  deal_product_id bigint  NULL,                -- NULL for extension reports where dp not matched
  url             text    NOT NULL,
  method          text    NOT NULL,             -- 'http', 'playwright', 'extension'
  success         boolean NOT NULL DEFAULT false,
  price           numeric(12,2) NULL,
  currency        text    NULL,
  error_message   text    NULL,
  created_at      timestamptz NOT NULL DEFAULT now()
);

-- Indexes for the admin report queries
CREATE INDEX IF NOT EXISTS idx_scrape_log_store_id      ON scrape_log(store_id);
CREATE INDEX IF NOT EXISTS idx_scrape_log_created_at    ON scrape_log(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_scrape_log_store_method  ON scrape_log(store_id, method);

-- 3. Auto-purge old rows (optional — keep 30 days)
-- You can run: DELETE FROM scrape_log WHERE created_at < now() - interval '30 days';
-- via a scheduled function, or skip if table size is acceptable.
