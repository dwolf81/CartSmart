-- Preload variant attribute details/counts directly in main deal results.
-- This keeps variant modal options aligned with current deal filters
-- (store, condition, deal type, and attribute filters) without on-demand fetch.

create or replace function f_get_product_deals_2(
  p_product_id integer,
  p_user_id integer = null,
  p_store_id bigint = null,
  p_deal_type_id bigint = null,
  p_condition_id bigint = null,
  p_attribute_filters jsonb = null
)
returns table (
  deal_product_id bigint,
  product_variant_id bigint,
  variant_details character varying,
  deal_variant_count integer,
  deal_id bigint,
  deal_type_id bigint,
  condition_id bigint,
  deal_status_id smallint,
  deal_status_name character varying,
  created_at timestamp without time zone,
  price numeric,
  url character varying,
  external_offer_url character varying,
  coupon_code character varying,
  additional_details character varying,
  discount_percent smallint,
  free_shipping boolean,
  first_name character varying,
  last_name character varying,
  user_name character varying,
  user_image_url character varying,
  level smallint,
  condition_name character varying,
  deal_type_name character varying,
  product_name character varying,
  product_id bigint,
  msrp numeric,
  product_image_url character varying,
  slug character varying,
  store_image_url character varying,
  store_url character varying,
  store_name character varying,
  store_id bigint,
  upfront_cost numeric,
  upfront_cost_term_id smallint,
  external_store_url character varying,
  external_upfront_cost numeric,
  external_upfront_cost_term_id smallint,
  affiliate_code character varying,
  affiliate_code_var character varying,
  external_affiliate_code character varying,
  external_affiliate_code_var character varying,
  steps jsonb,
  store_deal_count integer,
  additional_deal_count integer
) as $$
begin
  return query
  with
  filter_raw as (
    select jsonb_array_elements(coalesce(p_attribute_filters, '[]'::jsonb)) as obj
  ),
  filter_attrs as (
    select distinct (obj->>'attribute_id')::bigint as attribute_id
    from filter_raw
    where obj ? 'attribute_id'
  ),
  filter_values as (
    select
      (obj->>'attribute_id')::bigint as attribute_id,
      (val)::bigint as enum_value_id
    from filter_raw
    cross join lateral jsonb_array_elements_text(coalesce(obj->'enum_value_ids', '[]'::jsonb)) as val
  ),
  base as (
    select
      dp.id as deal_product_id,
      dp.product_variant_id,
      (
        select string_agg(v.attr_text, ' • ' order by v.attr_label)
        from (
          select
            coalesce(nullif(a.description, ''), a.attribute_key, ('Attribute ' || a.id::text)) as attr_label,
            coalesce(nullif(a.description, ''), a.attribute_key, ('Attribute ' || a.id::text)) || ': ' ||
            string_agg(
              coalesce(nullif(aev.display_name, ''), aev.enum_key, aev.id::text),
              ', ' order by coalesce(aev.sort_order, 2147483647), coalesce(aev.display_name, aev.enum_key, aev.id::text)
            ) as attr_text
          from product_variant_attribute pva
          join attribute a on a.id = pva.attribute_id
          left join attribute_enum_value aev on aev.id = pva.enum_value_id
          where pva.product_variant_id = dp.product_variant_id
          group by a.id, a.description, a.attribute_key
        ) v
      )::varchar as variant_details,
      d.id  as deal_id,
      d.deal_type_id,
      dp.condition_id,
      d.deal_status_id,
      ds.name as deal_status_name,
      d.created_at::timestamp without time zone as created_at,
      dp.price,
      dp.url,
      d.external_offer_url,
      d.coupon_code,
      d.additional_details,
      d.discount_percent,
      dp.free_shipping,
      u.first_name,
      u.last_name,
      u.user_name,
      replace(u.image_url,'_100x100.webp','_32x32.webp')::varchar as user_image_url,
      u.level,
      c.name  as condition_name,
      dt.name as deal_type_name,
      p.name as product_name,
      p.id   as product_id,
      p.msrp,
      p.image_url as product_image_url,
      p.slug,
      s.image_url as store_image_url,
      coalesce(s.url, se.url) as store_url,
      coalesce(s.name,  se.name)  as store_name,
      coalesce(s.id,  se.id)  as store_id,
      coalesce(s.upfront_cost, se.upfront_cost) as upfront_cost,
      coalesce(s.upfront_cost_term_id, se.upfront_cost_term_id) as upfront_cost_term_id,
      se.url as external_store_url,
      se.upfront_cost as external_upfront_cost,
      se.upfront_cost_term_id as external_upfront_cost_term_id,
      s.affiliate_code,
      s.affiliate_code_var,
      se.affiliate_code as external_affiliate_code,
      se.affiliate_code_var as external_affiliate_code_var,
      (
        select jsonb_agg(to_jsonb(sd) - 'product_id' - 'created_at' - 'updated_at')
        from (
          select
            sdp.id as deal_product_id,
            sdp.product_variant_id,
            (
              select string_agg(sv.attr_text, ' • ' order by sv.attr_label)
              from (
                select
                  coalesce(nullif(sa.description, ''), sa.attribute_key, ('Attribute ' || sa.id::text)) as attr_label,
                  coalesce(nullif(sa.description, ''), sa.attribute_key, ('Attribute ' || sa.id::text)) || ': ' ||
                  string_agg(
                    coalesce(nullif(saev.display_name, ''), saev.enum_key, saev.id::text),
                    ', ' order by coalesce(saev.sort_order, 2147483647), coalesce(saev.display_name, saev.enum_key, saev.id::text)
                  ) as attr_text
                from product_variant_attribute spva
                join attribute sa on sa.id = spva.attribute_id
                left join attribute_enum_value saev on saev.id = spva.enum_value_id
                where spva.product_variant_id = sdp.product_variant_id
                group by sa.id, sa.description, sa.attribute_key
              ) sv
            )::varchar as variant_details,
            sd.id as deal_id,
            sd.coupon_code,
            dt2.name as deal_type_name,
            sd.deal_type_id,
            sdp.url,
            sd.external_offer_url,
            sd.additional_details,
            sd.discount_percent,
            ss.url as store_url,
            ss.upfront_cost_term_id,
            sse.url as external_store_url,
            sse.upfront_cost as external_upfront_cost,
            sse.upfront_cost_term_id as external_upfront_cost_term_id,
            ss.affiliate_code,
            ss.affiliate_code_var,
            sse.affiliate_code as external_affiliate_code,
            sse.affiliate_code_var as external_affiliate_code_var
          from deal_combo sdc
          join deal sd on sd.id = sdc.combo_deal_id
          join deal_product sdp on sdp.deal_id = sd.id and sdp.product_id = p_product_id
          join deal_type dt2 on dt2.id = sd.deal_type_id
          left join store ss  on ss.id  = sd.store_id
          left join store sse on sse.id = sd.external_offer_store_id
          where sdc.deal_id = d.id
          order by sdc.order
        ) sd
      ) as steps

    from deal d
    join public.user u on d.user_id = u.id
    join deal_product dp on dp.deal_id = d.id
    join product p on dp.product_id = p.id
    join condition c on c.id = dp.condition_id
    join deal_type dt on dt.id = d.deal_type_id
    join deal_status ds on ds.id = d.deal_status_id
    left join store s  on s.id  = d.store_id
    left join store se on se.id = d.external_offer_store_id

    where
      d.deleted = false
      and dp.deleted = false
      and (d.expiration_date is null or d.expiration_date > now())
      and dp.product_id = p_product_id
      and (
        (d.deal_status_id = 2 and dp.deal_status_id = 2)
        or (d.user_id = p_user_id and d.deal_status_id in (1,5))
      )
      and (p_deal_type_id is null or d.deal_type_id = p_deal_type_id)
      and (p_condition_id is null or dp.condition_id = p_condition_id)
      and (
        p_store_id is null
        or coalesce(d.store_id, d.external_offer_store_id) = p_store_id
      )
      and (
        (select count(*) from filter_attrs) = 0
        or (
          dp.product_variant_id is not null
          and (
            select count(*)::int
            from filter_attrs fa
            where exists (
              select 1
              from product_variant_attribute pva
              join filter_values fv
                on fv.attribute_id = pva.attribute_id
               and fv.enum_value_id = pva.enum_value_id
              where pva.product_variant_id = dp.product_variant_id
                and pva.attribute_id = fa.attribute_id
            )
          ) = (select count(*) from filter_attrs)
        )
      )
  ),
  deal_variant_counts as (
    select
      b.deal_id,
      case
        when count(distinct b.product_variant_id) > 0 then count(distinct b.product_variant_id)
        else count(*)
      end::int as deal_variant_count
    from base b
    group by b.deal_id
  ),
  ranked as (
    select
      b.*,
      dvc.deal_variant_count,
      row_number() over (
        partition by b.store_id
        order by b.price asc, b.created_at desc, b.deal_id asc
      ) as rn,
      min(b.price) over (partition by b.store_id) as store_primary_price,
      (count(*) over (partition by b.store_id))::int as store_deal_count
    from base b
    join deal_variant_counts dvc on dvc.deal_id = b.deal_id
    where b.store_id is not null
  ),
  filtered as (
    select
      r.*,
      greatest(r.store_deal_count - 1, 0)::int as additional_deal_count
    from ranked r
    where
      (p_store_id is null and r.rn = 1)
      or (p_store_id is not null and r.rn <= 5)
  )
  select
    f.deal_product_id,
    f.product_variant_id,
    f.variant_details,
    f.deal_variant_count,
    f.deal_id,
    f.deal_type_id,
    f.condition_id,
    f.deal_status_id,
    f.deal_status_name,
    f.created_at,
    f.price,
    f.url,
    f.external_offer_url,
    f.coupon_code,
    f.additional_details,
    f.discount_percent,
    f.free_shipping,
    f.first_name,
    f.last_name,
    f.user_name,
    f.user_image_url,
    f.level,
    f.condition_name,
    f.deal_type_name,
    f.product_name,
    f.product_id,
    f.msrp,
    f.product_image_url,
    f.slug,
    f.store_image_url,
    f.store_url,
    f.store_name,
    f.store_id,
    f.upfront_cost,
    f.upfront_cost_term_id,
    f.external_store_url,
    f.external_upfront_cost,
    f.external_upfront_cost_term_id,
    f.affiliate_code,
    f.affiliate_code_var,
    f.external_affiliate_code,
    f.external_affiliate_code_var,
    f.steps,
    f.store_deal_count,
    f.additional_deal_count
  from filtered f
  order by
    f.store_primary_price asc,
    f.store_id asc,
    f.rn asc;

end;
$$ language plpgsql;
