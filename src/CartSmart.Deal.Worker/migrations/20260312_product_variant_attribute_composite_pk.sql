-- Allow multiple enum values per attribute on a single variant.
-- Old PK: (product_variant_id, attribute_id)
-- New PK: (product_variant_id, attribute_id, enum_value_id)

ALTER TABLE product_variant_attribute
  DROP CONSTRAINT product_variant_attribute_pkey;

ALTER TABLE product_variant_attribute
  ADD PRIMARY KEY (product_variant_id, attribute_id, enum_value_id);
