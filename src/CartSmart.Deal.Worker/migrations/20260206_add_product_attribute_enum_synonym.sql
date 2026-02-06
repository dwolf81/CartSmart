-- Per-product synonyms for attribute enum values (used for variant resolution during ingestion).
-- Example: attribute "ball_count" enum value "12" might have synonyms like "dozen" and "1 dozen".

create table if not exists product_attribute_enum_synonym (
  id bigserial primary key,
  product_id integer not null references product(id) on delete cascade,
  attribute_id integer not null references attribute(id) on delete cascade,
  enum_value_id integer not null references attribute_enum_value(id) on delete cascade,
  synonym text not null,
  normalized_synonym text generated always as (lower(regexp_replace(synonym, '[^a-zA-Z0-9]+', '', 'g'))) stored,
  is_active boolean not null default true,
  created_at timestamptz not null default now()
);

create index if not exists ix_product_attribute_enum_synonym_product_enum_active
  on product_attribute_enum_synonym(product_id, enum_value_id, is_active);

create index if not exists ix_product_attribute_enum_synonym_product_attr
  on product_attribute_enum_synonym(product_id, attribute_id);

create unique index if not exists ux_product_attribute_enum_synonym_product_enum_norm
  on product_attribute_enum_synonym(product_id, enum_value_id, normalized_synonym);
