-- Returns deals for review/profile contexts.
-- Includes store metadata and ensures url falls back to store.url when deal_product.url is null
-- (needed for store-wide deals that have no deal_product row).

create or replace function f_get_deals_review(
  p_user_id bigint,
  p_mode text
)
returns table (
  url character varying,
  additional_details character varying,
  condition_name character varying,
  condition_id bigint,
  price numeric,
  level integer,
  user_name character varying,
  display_name character varying,
  slug character varying,
  product_name character varying,
  deal_id bigint,
  product_id bigint,
  store_id bigint,
  store_name character varying,
  store_image_url character varying,
  deal_product_id bigint,
  product_variant_id integer,
  discount_amt numeric,
  discount_percent numeric,
  product_image_url character varying,
  msrp numeric,
  brand_name character varying,
  user_image_url character varying,
  deal_type_name character varying,
  coupon_code character varying,
  free_shipping boolean,
  deal_status_id integer,
  deal_status_name character varying,
  deal_type_id integer,
  created_at timestamptz,
  expiration_date timestamptz,
  user_flagged boolean,
  review_comment character varying,
  review_deal_status_id integer,
  store_url character varying,
  external_offer_store_url character varying,
  external_offer_url character varying,
  upfront_cost numeric,
  upfront_cost_term_id integer,
  external_store_url character varying,
  external_upfront_cost numeric,
  external_upfront_cost_term_id integer,
  parent_deal_id bigint
)
language sql
stable
as $$
  with base as (
    select
      d.id::bigint as deal_id,
      d.user_id::bigint as deal_user_id,
      d.created_at,
      d.expiration_date,
      d.additional_details,
      d.coupon_code,
      d.deal_status_id,
      d.deal_type_id,
      d.external_offer_url,
      d.external_offer_store_id,
      d.parent_deal_id,

      -- Prefer a primary dp if present; store-wide deals will have null dp.
      dp.id::bigint as deal_product_id,
      dp.product_id::bigint as product_id,
      dp.product_variant_id::int as product_variant_id,
      dp.price,
      dp.url as dp_url,
      dp.condition_id::bigint as condition_id,
      dp.free_shipping,

      p.slug,
      p.name as product_name,
      p.image_url as product_image_url,
      p.msrp,
      b.name as brand_name,

      u.level::integer as level,
      u.user_name,
      u.display_name,
      replace(u.image_url,'_100x100.webp','_32x32.webp')::varchar as user_image_url,

      c.name as condition_name,

      s.id::bigint as store_id,
      s.name as store_name,
      s.image_url as store_image_url,
      s.url as store_url,

      se.url as external_offer_store_url,
      se.url as external_store_url,

      case when se.upfront_cost is not null then se.upfront_cost else s.upfront_cost end as upfront_cost,
      case when se.upfront_cost_term_id is not null then se.upfront_cost_term_id else s.upfront_cost_term_id end as upfront_cost_term_id,
      se.upfront_cost as external_upfront_cost,
      se.upfront_cost_term_id as external_upfront_cost_term_id,

      ds.name as deal_status_name,
      dt.name as deal_type_name
    from deal d
    left join lateral (
      select dp1.*
      from deal_product dp1
      where dp1.deal_id = d.id
        and dp1.deleted = false
      order by dp1.primary desc, dp1.id asc
      limit 1
    ) dp on true
    left join product p on p.id = dp.product_id and p.deleted = false
    left join brand b on b.id = p.brand_id
    join "user" u on u.id = d.user_id
    left join condition c on c.id = dp.condition_id
    left join store s on s.id = d.store_id
    left join store se on se.id = d.external_offer_store_id
    left join deal_status ds on ds.id = d.deal_status_id
    left join deal_type dt on dt.id = d.deal_type_id
    where d.deleted = false
  )
  select
    coalesce(base.dp_url, base.store_url) as url,
    base.additional_details,
    base.condition_name,
    base.condition_id,
    base.price,
    base.level,
    base.user_name,
    base.display_name,
    base.slug,
    base.product_name,
    base.deal_id,
    base.product_id,
    base.store_id,
    base.store_name,
    base.store_image_url,
    base.deal_product_id,
    base.product_variant_id,
    case
      when base.msrp is not null and base.price is not null
        then (base.msrp - base.price)
      else null
    end as discount_amt,
    case
      when base.msrp is not null and base.price is not null and base.msrp > 0
        then ((base.msrp - base.price) / base.msrp) * 100
      else null
    end as discount_percent,
    base.product_image_url,
    base.msrp,
    base.brand_name,
    base.user_image_url,
    base.deal_type_name,
    base.coupon_code,
    base.free_shipping,
    base.deal_status_id,
    base.deal_status_name,
    base.deal_type_id,
    base.created_at,
    base.expiration_date,

    exists (
      select 1
      from deal_flag df
      where df.user_id = p_user_id
        and (
          df.deal_id = base.deal_id
          or (base.deal_product_id is not null and df.deal_product_id = base.deal_product_id)
        )
    ) as user_flagged,

    (
      select r.comments
      from deal_review r
      where r.user_id = p_user_id
        and r.deal_id = base.deal_id
        and (
          (r.deal_product_id is null and base.deal_product_id is null)
          or (r.deal_product_id is not null and r.deal_product_id = base.deal_product_id)
        )
      order by r.created_at desc
      limit 1
    ) as review_comment,

    (
      select r.deal_status_id
      from deal_review r
      where r.user_id = p_user_id
        and r.deal_id = base.deal_id
        and (
          (r.deal_product_id is null and base.deal_product_id is null)
          or (r.deal_product_id is not null and r.deal_product_id = base.deal_product_id)
        )
      order by r.created_at desc
      limit 1
    ) as review_deal_status_id,

    base.store_url,
    base.external_offer_store_url,
    base.external_offer_url,
    base.upfront_cost,
    base.upfront_cost_term_id,
    base.external_store_url,
    base.external_upfront_cost,
    base.external_upfront_cost_term_id,
    base.parent_deal_id
  from base
  where
    case
      when p_mode = 'Submitted' then base.deal_user_id = p_user_id
      when p_mode = 'Reviewed' then exists (
        select 1
        from deal_review r
        where r.user_id = p_user_id
          and r.deal_id = base.deal_id
          and (
            (r.deal_product_id is null and base.deal_product_id is null)
            or (r.deal_product_id is not null and r.deal_product_id = base.deal_product_id)
          )
      )
      when p_mode = 'Not Reviewed' then not exists (
        select 1
        from deal_review r
        where r.user_id = p_user_id
          and r.deal_id = base.deal_id
          and (
            (r.deal_product_id is null and base.deal_product_id is null)
            or (r.deal_product_id is not null and r.deal_product_id = base.deal_product_id)
          )
      )
      else true
    end
  order by base.created_at desc, base.deal_id desc;
$$;
