-- Per-product alternate search names for store listing search (e.g., "Mevo+" vs "Mevo Plus").

create table if not exists product_search_alias (
  id bigserial primary key,
  product_id integer not null references product(id) on delete cascade,
  alias text not null,
  normalized_alias text generated always as (lower(regexp_replace(alias, '[^a-zA-Z0-9]+', '', 'g'))) stored,
  is_active boolean not null default true,
  created_at timestamptz not null default now()
);

create index if not exists ix_product_search_alias_product_active
  on product_search_alias(product_id, is_active);

create unique index if not exists ux_product_search_alias_product_norm
  on product_search_alias(product_id, normalized_alias);
