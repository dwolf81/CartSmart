-- Returns distinct product types that have qualifying deals for a given store.
-- Mirrors the same "qualifying" criteria used by f_best_deals:
-- - deal.deal_status_id = 2 and deal.deleted = false
-- - deal_product.deal_status_id = 2 and deal_product.deleted = false
-- - product.deleted = false
-- - deal.store_id = p_store_id

create or replace function f_store_product_types(
  p_store_id bigint
)
returns table (
  id integer,
  name character varying,
  slug character varying
)
language sql
stable
as $$
  select distinct
    pt.id,
    pt.name,
    pt.slug
  from deal d
  join deal_product dp on dp.deal_id = d.id
  join product p on p.id = dp.product_id
  join product_type pt on pt.id = p.product_type_id
  where
    p_store_id is not null
    and d.store_id = p_store_id
    and d.deal_status_id = 2
    and dp.deal_status_id = 2
    and d.deleted = false
    and dp.deleted = false
    and p.deleted = false
  order by pt.name asc nulls last;
$$;
