-- Migration: Replace scrape_enabled boolean with scrape_mode_id integer
-- scrape_mode_id values:
--   0 = None       (no scraping)
--   1 = All        (backend service + browser extension)
--   2 = BrowserOnly (browser extension only; headless browsers blocked)

-- 1. Create the scrape_mode lookup table
CREATE TABLE IF NOT EXISTS scrape_mode (
    id integer PRIMARY KEY,
    name text NOT NULL,
    description text
);

INSERT INTO scrape_mode (id, name, description) VALUES
    (0, 'None',         'No scraping — prices only via API if api_enabled is true'),
    (1, 'All',          'Full scraping — both the backend service and the browser extension can scrape'),
    (2, 'Browser Only', 'Browser extension only — headless browsers are blocked by the store')
ON CONFLICT (id) DO NOTHING;

-- 2. Add the new column (default 0 = None) with FK to scrape_mode
ALTER TABLE store ADD COLUMN IF NOT EXISTS scrape_mode_id integer NOT NULL DEFAULT 0 REFERENCES scrape_mode(id);

-- 3. Back-fill from existing scrape_enabled: true → 1 (All), false/null → 0 (None)
UPDATE store SET scrape_mode_id = 1 WHERE scrape_enabled = true;

-- 4. Create index on the new column
CREATE INDEX IF NOT EXISTS idx_store_scrape_mode_id ON store (scrape_mode_id);

-- 5. Drop the old column and its index
DROP INDEX IF EXISTS idx_store_scrape_enabled;
ALTER TABLE store DROP COLUMN IF EXISTS scrape_enabled;
