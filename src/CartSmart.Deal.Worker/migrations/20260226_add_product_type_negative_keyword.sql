-- Product-type scoped negative keywords used to exclude store listings during ingestion
-- across all products in a category (product_type).

create table if not exists product_type_negative_keyword (
    id bigserial primary key,
    product_type_id bigint not null references product_type(id) on delete cascade,
    keyword text not null,
    normalized_keyword text generated always as (lower(btrim(keyword))) stored,
    is_active boolean not null default true,
    created_at timestamptz not null default now()
);

create unique index if not exists ux_product_type_negative_keyword_type_norm
    on product_type_negative_keyword (product_type_id, normalized_keyword);

create index if not exists ix_product_type_negative_keyword_type_active
    on product_type_negative_keyword (product_type_id)
    where is_active;
