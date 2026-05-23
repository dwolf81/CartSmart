-- Candidate ingestion pipeline: extension-submitted product candidates,
-- crawler/extension-submitted deal candidates, and admin-curated store scan endpoints
-- the listing crawler is allowed to hit.

-- ─────────────────────────────────────────────────────────────────────────────
-- product_candidate
--   A proposed new Product. Created by the Chrome extension when an admin
--   clicks "Add Product" on an approved retailer page.
--   Promoted into the live `product` table on admin approval, or marked merged
--   when admin chooses to attach the candidate's deal to an existing product.
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS product_candidate (
    id                          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    last_submitted_at           timestamptz NOT NULL DEFAULT now(),

    -- Provenance
    source                      text NOT NULL DEFAULT 'extension',   -- extension | crawler | ai
    source_store_id             int  NOT NULL REFERENCES store(id),
    source_url_canonical        text NOT NULL,

    -- Scraped product fields (free-text first; FK fields filled by admin or auto-match)
    name                        text NOT NULL,
    name_normalized             text NOT NULL,                       -- lowercased, alphanum + single-space
    brand_text                  text,
    brand_id                    int  REFERENCES brand(id),
    product_type_id             int  REFERENCES product_type(id),
    msrp                        numeric(10,2),
    slug_suggested              text,
    image_url_original          text,                                -- as scraped from retailer
    image_url                   text,                                -- rehosted in the "candidates" bucket
    description                 text,

    -- Review state
    status                      text NOT NULL DEFAULT 'pending_review',  -- pending_review | approved | rejected | duplicate | merged
    suggested_merge_product_id  int  REFERENCES product(id),
    merged_into_product_id      int  REFERENCES product(id),
    admin_notes                 text,

    -- Popularity signal
    submitted_by_user_id        int  REFERENCES "user"(id),
    submission_count            int  NOT NULL DEFAULT 1,
    submitters_jsonb            jsonb NOT NULL DEFAULT '[]'::jsonb
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_product_candidate_url
    ON product_candidate(source_url_canonical);
CREATE INDEX IF NOT EXISTS ix_product_candidate_status
    ON product_candidate(status);
CREATE INDEX IF NOT EXISTS ix_product_candidate_brand_name
    ON product_candidate(brand_id, name_normalized);
CREATE INDEX IF NOT EXISTS ix_product_candidate_created_at
    ON product_candidate(created_at DESC);


-- ─────────────────────────────────────────────────────────────────────────────
-- deal_candidate
--   A proposed deal. Either tied to a product_candidate (extension flow)
--   or to an existing product (crawler / AI flow).
--   Promoted into `deal` + `deal_product` on admin approval.
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS deal_candidate (
    id                          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    last_seen_at                timestamptz NOT NULL DEFAULT now(),

    source                      text NOT NULL,                       -- extension | crawler | ai
    store_id                    int  NOT NULL REFERENCES store(id),
    product_candidate_id        bigint REFERENCES product_candidate(id) ON DELETE CASCADE,
    product_id                  int  REFERENCES product(id),

    deal_url_canonical          text NOT NULL,
    listing_price               numeric(10,2),
    listing_currency            text DEFAULT 'USD',
    listing_msrp                numeric(10,2),
    condition_category_id       int,
    in_stock                    bool,
    raw_title                   text,
    raw_html_snippet            text,
    ai_confidence               numeric(4,3),

    -- pending_review | approved | rejected | promoted
    status                      text NOT NULL DEFAULT 'pending_review',
    promoted_deal_id            int  REFERENCES deal(id),
    admin_notes                 text,

    CONSTRAINT deal_candidate_requires_owner
        CHECK (product_candidate_id IS NOT NULL OR product_id IS NOT NULL)
);

-- Allow at most one pending-review row per canonical URL.
-- Approved/rejected/promoted rows are kept for audit and may share the same URL
-- with a freshly resubmitted listing.
CREATE UNIQUE INDEX IF NOT EXISTS ux_deal_candidate_url_pending
    ON deal_candidate(deal_url_canonical)
    WHERE status = 'pending_review';
CREATE INDEX IF NOT EXISTS ix_deal_candidate_status
    ON deal_candidate(status);
CREATE INDEX IF NOT EXISTS ix_deal_candidate_product
    ON deal_candidate(product_id);
CREATE INDEX IF NOT EXISTS ix_deal_candidate_store
    ON deal_candidate(store_id);
CREATE INDEX IF NOT EXISTS ix_deal_candidate_product_candidate
    ON deal_candidate(product_candidate_id);
CREATE INDEX IF NOT EXISTS ix_deal_candidate_created_at
    ON deal_candidate(created_at DESC);


-- ─────────────────────────────────────────────────────────────────────────────
-- store_scan_endpoint
--   Admin-curated listing-index URLs per store. The discovery crawler only
--   hits URLs that appear here (mirrors product_store_page for the
--   product-pinned pass). product_type_id scopes the fuzzy-match candidate
--   set so e.g. a "clearance irons" page only matches against iron products.
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS store_scan_endpoint (
    id                  int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    store_id            int  NOT NULL REFERENCES store(id) ON DELETE CASCADE,
    url                 text NOT NULL,
    label               text,                                          -- e.g. "Clearance irons", "Sale putters"
    product_type_id     int  REFERENCES product_type(id),              -- scope (NULL = all product types)
    is_active           bool NOT NULL DEFAULT true,
    last_crawled_at     timestamptz,
    last_result_count   int,
    created_at          timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_store_scan_endpoint_store_url
    ON store_scan_endpoint(store_id, url);
CREATE INDEX IF NOT EXISTS ix_store_scan_endpoint_active
    ON store_scan_endpoint(is_active) WHERE is_active = true;
