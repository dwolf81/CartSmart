-- product_store_page: maps a product to a specific HTML page on a store
-- for scraping listings (new/used/refurbished).
CREATE TABLE IF NOT EXISTS product_store_page (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id      int NOT NULL REFERENCES product(id) ON DELETE CASCADE,
    store_id        int NOT NULL REFERENCES store(id) ON DELETE CASCADE,
    url             text NOT NULL,
    enabled         boolean NOT NULL DEFAULT true,
    last_scraped_at timestamptz,
    scrape_interval_minutes int NOT NULL DEFAULT 120,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_product_store_page_product_id ON product_store_page(product_id);
CREATE INDEX IF NOT EXISTS idx_product_store_page_store_id ON product_store_page(store_id);
CREATE INDEX IF NOT EXISTS idx_product_store_page_enabled_due ON product_store_page(enabled, last_scraped_at);
CREATE UNIQUE INDEX IF NOT EXISTS idx_product_store_page_product_store_url ON product_store_page(product_id, store_id, url);

-- RLS disabled to match other core tables (deal, product, store, etc.)
-- The worker uses the anon key and cannot access service_role-only policies.
-- ALTER TABLE product_store_page ENABLE ROW LEVEL SECURITY;
