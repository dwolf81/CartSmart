-- Adds per-product enum disabling for product attributes.
-- By default, all enums for an attached attribute are considered enabled.
-- Rows in this table represent enums that are disabled (not applicable) for a given product.

create table if not exists product_attribute_enum_disabled (
  product_id integer not null references product(id) on delete cascade,
  attribute_id integer not null references attribute(id) on delete cascade,
  enum_value_id integer not null references attribute_enum_value(id) on delete cascade,
  created_at timestamptz not null default now(),
  primary key (product_id, enum_value_id)
);

create index if not exists ix_product_attribute_enum_disabled_product_attr
  on product_attribute_enum_disabled(product_id, attribute_id);
