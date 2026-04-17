-- =============================================================================
-- Sender Verification + Multi-Deal + Store-Wide Support
-- =============================================================================

-- Link ingestion sources to a specific store for sender verification
ALTER TABLE ingestion_source
  ADD COLUMN IF NOT EXISTS store_id integer REFERENCES store(id);

-- Track whether an extracted deal is store-wide
ALTER TABLE extracted_deal
  ADD COLUMN IF NOT EXISTS store_wide boolean NOT NULL DEFAULT false;

-- Index for quick lookup of sources by store
CREATE INDEX IF NOT EXISTS idx_ingestion_source_store ON ingestion_source(store_id) WHERE store_id IS NOT NULL;
