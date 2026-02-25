-- Add an optional API minimum price override to the product table.
-- When set (NOT NULL), this value is used as the minimum price filter
-- in eBay Browse API search calls instead of the default 30%-of-MSRP calculation.
-- This allows per-product tuning to exclude low-cost accessories.

ALTER TABLE product
ADD COLUMN IF NOT EXISTS api_min_price NUMERIC(10, 2) DEFAULT NULL;

COMMENT ON COLUMN product.api_min_price IS 'Optional override for the minimum price sent to the eBay Browse API search filter. When NULL the default (30% of MSRP) is used.';
