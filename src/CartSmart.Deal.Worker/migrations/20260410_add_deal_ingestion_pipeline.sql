-- =============================================================================
-- Deal Ingestion Pipeline
-- Tables: ingestion_source, raw_signal, extracted_deal
-- =============================================================================

-- ingestion_source — registered feed/source configurations
create table if not exists ingestion_source (
  id            bigserial   primary key,
  created_at    timestamptz not null default now(),
  name          text        not null,                       -- human label e.g. "Reddit /r/buildapcsales"
  source_type   text        not null,                       -- email | reddit | social | retail | forum
  config        jsonb       not null default '{}'::jsonb,   -- source-specific settings (subreddit, account, URL, etc.)
  enabled       boolean     not null default true,
  poll_interval_minutes integer not null default 30,
  last_polled_at timestamptz
);

-- raw_signal — every piece of content collected from sources before AI extraction
create table if not exists raw_signal (
  id                bigserial   primary key,
  created_at        timestamptz not null default now(),
  ingestion_source_id bigint    not null references ingestion_source(id),
  external_id       text,                                   -- source-native ID (reddit post id, tweet id, email message-id, etc.)
  title             text,
  body              text,
  url               text,
  author            text,
  raw_json          jsonb,                                  -- full original payload for audit/reprocessing
  status            text        not null default 'pending', -- pending | processing | extracted | failed | duplicate
  error_message     text,
  processed_at      timestamptz,
  unique (ingestion_source_id, external_id)
);

-- extracted_deal — AI-parsed deal data from raw signals, pending review or auto-import
create table if not exists extracted_deal (
  id                bigserial     primary key,
  created_at        timestamptz   not null default now(),
  raw_signal_id     bigint        not null references raw_signal(id),
  product_id        bigint        references product(id),       -- matched product (null if unmatched)
  store_id          integer       references store(id),         -- matched store (null if unmatched)
  deal_id           bigint        references deal(id),          -- set after import
  title             text          not null,
  price             numeric(12,2),
  currency          text          default 'USD',
  coupon_code       text,
  url               text,
  discount_percent  integer,
  deal_type_id      integer       default 1,                    -- 1=Direct, 2=Coupon, 3=Stacked, 4=External
  expiration_date   timestamptz,
  confidence_score  numeric(5,4) not null default 0,            -- 0.0000–1.0000
  ai_reasoning      text,
  status            text          not null default 'pending_review', -- pending_review | auto_imported | manually_imported | rejected | expired
  reviewed_by       bigint        references "user"(id),
  reviewed_at       timestamptz,
  imported_at       timestamptz
);

-- Indexes for common query patterns
create index if not exists idx_raw_signal_status on raw_signal(status) where status = 'pending';
create index if not exists idx_raw_signal_source on raw_signal(ingestion_source_id, created_at desc);
create index if not exists idx_extracted_deal_status on extracted_deal(status) where status in ('pending_review', 'auto_imported');
create index if not exists idx_extracted_deal_product on extracted_deal(product_id) where product_id is not null;
create index if not exists idx_extracted_deal_signal on extracted_deal(raw_signal_id);
