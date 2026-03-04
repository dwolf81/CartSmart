-- Add product-level item count fields.
-- When count_enabled = true, the product supports per-item pricing (e.g. golf balls, batteries).
-- default_count stores the standard pack size (e.g. 12 for a dozen golf balls).
-- deal_product.item_count stores the actual count for each deal listing.

-- 1) Add columns to the product table
ALTER TABLE product
  ADD COLUMN IF NOT EXISTS count_enabled boolean NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS default_count integer NOT NULL DEFAULT 1;

-- 2) Add item_count column to deal_product table
--    Stores the actual number of items in this specific deal listing.
--    Defaults to 1 (single item).
ALTER TABLE deal_product
  ADD COLUMN IF NOT EXISTS item_count integer NOT NULL DEFAULT 1;

-- 3) Update f_get_product_deals_2 to include count fields in the result set.
--    The function now returns count_enabled, default_count (from product),
--    and item_count (from deal_product) so the frontend can compute price-per-item.
--    When count_enabled is true, ordering uses price/item_count (price per item) instead of raw price.

CREATE OR REPLACE FUNCTION f_get_product_deals_2(
  p_product_id integer,
  p_user_id integer DEFAULT NULL,
  p_store_id bigint DEFAULT NULL,
  p_deal_type_id bigint DEFAULT NULL,
  p_condition_id bigint DEFAULT NULL,
  p_attribute_filters jsonb DEFAULT NULL
)
RETURNS TABLE (
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
  additional_deal_count integer,
  count_enabled boolean,
  default_count integer,
  item_count integer
) AS $$
BEGIN
  RETURN QUERY
  WITH
  filter_raw AS (
    SELECT jsonb_array_elements(coalesce(p_attribute_filters, '[]'::jsonb)) AS obj
  ),
  filter_attrs AS (
    SELECT DISTINCT (obj->>'attribute_id')::bigint AS attribute_id
    FROM filter_raw
    WHERE obj ? 'attribute_id'
  ),
  filter_values AS (
    SELECT
      (obj->>'attribute_id')::bigint AS attribute_id,
      (val)::bigint AS enum_value_id
    FROM filter_raw
    CROSS JOIN LATERAL jsonb_array_elements_text(coalesce(obj->'enum_value_ids', '[]'::jsonb)) AS val
  ),
  base AS (
    SELECT
      dp.id AS deal_product_id,
      dp.product_variant_id,
      (
        SELECT string_agg(v.attr_text, ' • ' ORDER BY v.attr_label)
        FROM (
          SELECT
            coalesce(nullif(a.description, ''), a.attribute_key, ('Attribute ' || a.id::text)) AS attr_label,
            coalesce(nullif(a.description, ''), a.attribute_key, ('Attribute ' || a.id::text)) || ': ' ||
            string_agg(
              coalesce(nullif(aev.display_name, ''), aev.enum_key, aev.id::text),
              ', ' ORDER BY coalesce(aev.sort_order, 2147483647), coalesce(aev.display_name, aev.enum_key, aev.id::text)
            ) AS attr_text
          FROM product_variant_attribute pva
          JOIN attribute a ON a.id = pva.attribute_id
          LEFT JOIN attribute_enum_value aev ON aev.id = pva.enum_value_id
          WHERE pva.product_variant_id = dp.product_variant_id
          GROUP BY a.id, a.description, a.attribute_key
        ) v
      )::varchar AS variant_details,
      d.id  AS deal_id,
      d.deal_type_id,
      dp.condition_id,
      d.deal_status_id,
      ds.name AS deal_status_name,
      d.created_at::timestamp without time zone AS created_at,
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
      replace(u.image_url,'_100x100.webp','_32x32.webp')::varchar AS user_image_url,
      u.level,
      c.name  AS condition_name,
      dt.name AS deal_type_name,
      p.name AS product_name,
      p.id   AS product_id,
      p.msrp,
      p.image_url AS product_image_url,
      p.slug,
      s.image_url AS store_image_url,
      coalesce(s.url, se.url) AS store_url,
      coalesce(s.name,  se.name)  AS store_name,
      coalesce(s.id,  se.id)  AS store_id,
      coalesce(s.upfront_cost, se.upfront_cost) AS upfront_cost,
      coalesce(s.upfront_cost_term_id, se.upfront_cost_term_id) AS upfront_cost_term_id,
      se.url AS external_store_url,
      se.upfront_cost AS external_upfront_cost,
      se.upfront_cost_term_id AS external_upfront_cost_term_id,
      s.affiliate_code,
      s.affiliate_code_var,
      se.affiliate_code AS external_affiliate_code,
      se.affiliate_code_var AS external_affiliate_code_var,
      (
        SELECT jsonb_agg(to_jsonb(sd) - 'product_id' - 'created_at' - 'updated_at')
        FROM (
          SELECT
            sdp.id AS deal_product_id,
            sdp.product_variant_id,
            (
              SELECT string_agg(sv.attr_text, ' • ' ORDER BY sv.attr_label)
              FROM (
                SELECT
                  coalesce(nullif(sa.description, ''), sa.attribute_key, ('Attribute ' || sa.id::text)) AS attr_label,
                  coalesce(nullif(sa.description, ''), sa.attribute_key, ('Attribute ' || sa.id::text)) || ': ' ||
                  string_agg(
                    coalesce(nullif(saev.display_name, ''), saev.enum_key, saev.id::text),
                    ', ' ORDER BY coalesce(saev.sort_order, 2147483647), coalesce(saev.display_name, saev.enum_key, saev.id::text)
                  ) AS attr_text
                FROM product_variant_attribute spva
                JOIN attribute sa ON sa.id = spva.attribute_id
                LEFT JOIN attribute_enum_value saev ON saev.id = spva.enum_value_id
                WHERE spva.product_variant_id = sdp.product_variant_id
                GROUP BY sa.id, sa.description, sa.attribute_key
              ) sv
            )::varchar AS variant_details,
            sd.id AS deal_id,
            sd.coupon_code,
            dt2.name AS deal_type_name,
            sd.deal_type_id,
            sdp.url,
            sd.external_offer_url,
            sd.additional_details,
            sd.discount_percent,
            ss.url AS store_url,
            ss.upfront_cost_term_id,
            sse.url AS external_store_url,
            sse.upfront_cost AS external_upfront_cost,
            sse.upfront_cost_term_id AS external_upfront_cost_term_id,
            ss.affiliate_code,
            ss.affiliate_code_var,
            sse.affiliate_code AS external_affiliate_code,
            sse.affiliate_code_var AS external_affiliate_code_var
          FROM deal_combo sdc
          JOIN deal sd ON sd.id = sdc.combo_deal_id
          JOIN deal_product sdp ON sdp.deal_id = sd.id AND sdp.product_id = p_product_id
          JOIN deal_type dt2 ON dt2.id = sd.deal_type_id
          LEFT JOIN store ss  ON ss.id  = sd.store_id
          LEFT JOIN store sse ON sse.id = sd.external_offer_store_id
          WHERE sdc.deal_id = d.id
          ORDER BY sdc.order
        ) sd
      ) AS steps,
      p.count_enabled,
      p.default_count,
      dp.item_count

    FROM deal d
    JOIN public.user u ON d.user_id = u.id
    JOIN deal_product dp ON dp.deal_id = d.id
    JOIN product p ON dp.product_id = p.id
    JOIN condition c ON c.id = dp.condition_id
    JOIN deal_type dt ON dt.id = d.deal_type_id
    JOIN deal_status ds ON ds.id = d.deal_status_id
    LEFT JOIN store s  ON s.id  = d.store_id
    LEFT JOIN store se ON se.id = d.external_offer_store_id

    WHERE
      d.deleted = false
      AND dp.deleted = false
      AND (d.expiration_date IS NULL OR d.expiration_date > now())
      AND dp.product_id = p_product_id
      AND (
        (d.deal_status_id = 2 AND dp.deal_status_id = 2)
        OR (d.user_id = p_user_id AND d.deal_status_id IN (1,5))
      )
      AND (p_deal_type_id IS NULL OR d.deal_type_id = p_deal_type_id)
      AND (p_condition_id IS NULL OR dp.condition_id = p_condition_id)
      AND (
        p_store_id IS NULL
        OR coalesce(d.store_id, d.external_offer_store_id) = p_store_id
      )
      AND (
        (SELECT count(*) FROM filter_attrs) = 0
        OR (
          dp.product_variant_id IS NOT NULL
          AND (
            SELECT count(*)::int
            FROM filter_attrs fa
            WHERE EXISTS (
              SELECT 1
              FROM product_variant_attribute pva
              JOIN filter_values fv
                ON fv.attribute_id = pva.attribute_id
               AND fv.enum_value_id = pva.enum_value_id
              WHERE pva.product_variant_id = dp.product_variant_id
                AND pva.attribute_id = fa.attribute_id
            )
          ) = (SELECT count(*) FROM filter_attrs)
        )
      )
  ),
  deal_variant_counts AS (
    SELECT
      b.deal_id,
      CASE
        WHEN count(DISTINCT b.product_variant_id) > 0 THEN count(DISTINCT b.product_variant_id)
        ELSE count(*)
      END::int AS deal_variant_count
    FROM base b
    GROUP BY b.deal_id
  ),
  store_deal_counts AS (
    SELECT
      b.store_id,
      count(DISTINCT b.deal_id)::int AS store_deal_count
    FROM base b
    WHERE b.store_id IS NOT NULL
    GROUP BY b.store_id
  ),
  ranked AS (
    SELECT
      b.*,
      dvc.deal_variant_count,
      row_number() OVER (
        PARTITION BY b.store_id
        ORDER BY
          CASE WHEN b.count_enabled THEN b.price / NULLIF(b.item_count, 0) ELSE b.price END ASC,
          b.created_at DESC,
          b.deal_id ASC
      ) AS rn,
      min(CASE WHEN b.count_enabled THEN b.price / NULLIF(b.item_count, 0) ELSE b.price END) OVER (PARTITION BY b.store_id) AS store_primary_price,
      sdc.store_deal_count
    FROM base b
    JOIN deal_variant_counts dvc ON dvc.deal_id = b.deal_id
    JOIN store_deal_counts sdc ON sdc.store_id = b.store_id
    WHERE b.store_id IS NOT NULL
  ),
  filtered AS (
    SELECT
      r.*,
      greatest(r.store_deal_count - 1, 0)::int AS additional_deal_count
    FROM ranked r
    WHERE
      (p_store_id IS NULL AND r.rn = 1)
      OR (p_store_id IS NOT NULL AND r.rn <= 11)
  )
  SELECT
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
    f.additional_deal_count,
    f.count_enabled,
    f.default_count,
    f.item_count
  FROM filtered f
  ORDER BY
    f.store_primary_price ASC,
    f.store_id ASC,
    f.rn ASC;

END;
$$ LANGUAGE plpgsql;

-- 4) Update f_best_deals to include count fields and sort by per-item price when count_enabled.
CREATE OR REPLACE FUNCTION f_best_deals(
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
  upfront_cost_term_id smallint,
  count_enabled boolean,
  default_count integer,
  item_count integer
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
    case
      when p.count_enabled and p.default_count > 0 and coalesce(best.item_count, 1) > 0 then
        round((p.msrp / p.default_count) - (best.price / coalesce(best.item_count, 1)), 2)
      else
        (p.msrp - best.price)
    end as discount_amt,
    p.image_url as product_image_url,
    p.msrp,
    b.name as brand_name,
    p.name as product_name,
    best.upfront_cost,
    best.upfront_cost_term_id,
    p.count_enabled,
    p.default_count,
    best.item_count
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
      case when se.upfront_cost_term_id is not null then se.upfront_cost_term_id else s.upfront_cost_term_id end as upfront_cost_term_id,
      dp.item_count
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
      case when p.count_enabled then dp.price / nullif(dp.item_count, 0) else dp.price end asc nulls last,
      d.id asc
    limit 1
  ) best on true
  where
    (p_product_type_id is null or p.product_type_id = p_product_type_id)
    and (p_store_id is null or best.deal_id is not null)
    and (p_product_type_id is not null or best.deal_id is not null)
  order by
    case when p_product_type_id is not null then p.name end asc nulls last,
    case when p_product_type_id is null then
      case when p.count_enabled then best.price / nullif(best.item_count, 0) else best.price end
    end asc nulls last,
    case when p_product_type_id is null then best.deal_id end asc nulls last;
end;
$$;
