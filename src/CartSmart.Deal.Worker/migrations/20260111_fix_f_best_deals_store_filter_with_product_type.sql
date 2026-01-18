-- Fixes f_best_deals store filtering when both p_store_id and p_product_type_id are provided.
--
-- Desired behavior:
-- - If p_store_id IS NOT NULL: only return products that have a qualifying deal for that store.
-- - If p_store_id IS NULL: preserve existing behavior.
--   - If p_product_type_id IS NULL: only return products that have any qualifying deal.
--   - If p_product_type_id IS NOT NULL: return all products in that product type (even if no deals).

create or replace function f_best_deals(
  p_store_id bigint default null,
  p_product_type_id bigint default null
)
returns table (
  url character varying,
  additional_details character varying,
  price numeric,
  level integer,
  user_name character varying,
  user_image_url character varying,
  slug character varying,
  name character varying,
  deal_id bigint,
  product_id bigint,
  store_id bigint,
  discount_amt numeric,
  product_image_url character varying,
  msrp numeric,
  brand_name character varying,
  product_name character varying,
  upfront_cost numeric,
  upfront_cost_term_id smallint
)
language plpgsql
as $$
begin
  return query
  select
    best.url,
    best.additional_details,
    best.price,
    best.level,
    best.user_name,
    best.user_image_url,
    p.slug,
    p.name,
    best.deal_id,
    p.id as product_id,
    best.store_id,
    (p.msrp - best.price) as discount_amt,
    p.image_url as product_image_url,
    p.msrp,
    b.name as brand_name,
    p.name as product_name,
    best.upfront_cost,
    best.upfront_cost_term_id
  from product p
  join brand b on b.id = p.brand_id
  left join lateral (
    select
      dp.url,
      d.additional_details,
      dp.price,
      u.level::integer as level,
      u.user_name,
      replace(u.image_url,'_100x100.webp','_32x32.webp')::varchar as user_image_url,
      d.id as deal_id,
      d.store_id as store_id,
      case when se.upfront_cost is not null then se.upfront_cost else s.upfront_cost end as upfront_cost,
      case when se.upfront_cost_term_id is not null then se.upfront_cost_term_id else s.upfront_cost_term_id end as upfront_cost_term_id
    from deal d
    join deal_product dp on dp.deal_id = d.id
    join "user" u on u.id = d.user_id
    left join store s on s.id = d.store_id
    left join store se on se.id = d.external_offer_store_id
    where
      dp.product_id = p.id
      and d.deal_status_id = 2
      and dp.deal_status_id = 2
      and d.deleted = false
      and dp.deleted = false
      and (p_store_id is null or d.store_id = p_store_id)
    order by
      dp.price asc nulls last,
      d.id asc
    limit 1
  ) best on true
  where
    (p_product_type_id is null or p.product_type_id = p_product_type_id)
    -- If p_store_id is specified, only return products that have a deal for that store.
    and (p_store_id is null or best.deal_id is not null)
    -- If no product type filter, return only products that actually have a deal (any store).
    and (p_product_type_id is not null or best.deal_id is not null)
  order by
    case when p_product_type_id is not null then p.name end asc nulls last,
    case when p_product_type_id is null then best.price end asc nulls last,
    case when p_product_type_id is null then best.deal_id end asc nulls last;
end;
$$;
