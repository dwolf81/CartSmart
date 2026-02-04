-- Product-scoped negative keywords used to exclude store listings during ingestion
-- Example: product "Chrome Tour Golf Balls" with keyword "Triple Track" prevents matching listings containing that phrase.

create table if not exists product_negative_keyword (
    id bigserial primary key,
    product_id bigint not null references product(id) on delete cascade,
    keyword text not null,
    -- Normalized form used for dedup + fast contains checks in the worker.
    normalized_keyword text generated always as (lower(regexp_replace(keyword, '[^a-zA-Z0-9]+', '', 'g'))) stored,
    is_active boolean not null default true,
    created_at timestamptz not null default now()
);

create unique index if not exists ux_product_negative_keyword_product_norm
    on product_negative_keyword (product_id, normalized_keyword);

create index if not exists ix_product_negative_keyword_product_active
    on product_negative_keyword (product_id)
    where is_active;
