-- Add affiliate_url_template column to store table.
-- This supports wrapper-style affiliate URLs (e.g. Awin) using {url} and {url_encoded} placeholders.
-- When set, it overrides the legacy affiliate_code / affiliate_code_var single-param approach.

ALTER TABLE store ADD COLUMN IF NOT EXISTS affiliate_url_template text;

-- Recreate f_get_product_deals_2 to include affiliate_url_template in the return type and query.
DROP FUNCTION IF EXISTS public.f_get_product_deals_2(bigint, uuid, bigint, bigint, bigint, jsonb);
DROP FUNCTION IF EXISTS public.f_get_product_deals_2(bigint, integer, bigint, bigint, bigint, jsonb);
DROP FUNCTION IF EXISTS public.f_get_product_deals_2(integer, integer, bigint, bigint, bigint, jsonb);

CREATE OR REPLACE FUNCTION public.f_get_product_deals_2(
  p_product_id integer,
  p_user_id integer DEFAULT NULL,
  p_deal_type_id bigint DEFAULT NULL,
  p_condition_id bigint DEFAULT NULL,
  p_store_id bigint DEFAULT NULL,
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
  affiliate_url_template text,
  external_affiliate_code character varying,
  external_affiliate_code_var character varying,
  external_affiliate_url_template text,
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
      s.url AS store_url,
      s.name  AS store_name,
      s.id  AS store_id,
      s.upfront_cost AS upfront_cost,
      s.upfront_cost_term_id AS upfront_cost_term_id,
      se.url AS external_store_url,
      se.upfront_cost AS external_upfront_cost,
      se.upfront_cost_term_id AS external_upfront_cost_term_id,
      s.affiliate_code,
      s.affiliate_code_var,
      s.affiliate_url_template,
      se.affiliate_code AS external_affiliate_code,
      se.affiliate_code_var AS external_affiliate_code_var,
      se.affiliate_url_template AS external_affiliate_url_template,
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
            ss.affiliate_url_template,
            sse.affiliate_code AS external_affiliate_code,
            sse.affiliate_code_var AS external_affiliate_code_var,
            sse.affiliate_url_template AS external_affiliate_url_template
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
      -- Exclude deals this user has hidden
      AND (
        p_user_id IS NULL
        OR NOT EXISTS (
          SELECT 1 FROM hidden_deal hd
          WHERE hd.user_id = p_user_id AND hd.deal_id = d.id
        )
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
    f.affiliate_url_template,
    f.external_affiliate_code,
    f.external_affiliate_code_var,
    f.external_affiliate_url_template,
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
