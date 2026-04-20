-- Fix: sum stacked discount percentages instead of applying them sequentially.
-- Example: 20% + 15% = 35% off, so $47.99 * 0.65 = $31.19
-- (previously applied sequentially: $47.99 * 0.80 * 0.85 = $32.63)

DROP FUNCTION IF EXISTS public.f_upsert_stacked_deal_products_for_store(bigint, bigint);

CREATE OR REPLACE FUNCTION public.f_upsert_stacked_deal_products_for_store(
  p_stacked_deal_id bigint DEFAULT NULL,
  p_direct_deal_id bigint DEFAULT NULL
)
RETURNS int
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_count int := 0;
  v_rows int := 0;

  v_direct_store_id int;
  v_store_id int;
  v_store_count int;

  v_sw_total int;
  v_sw_in_stack int;

  v_base_product_id int;
  v_base_variant_id bigint;
  v_base_price numeric;
  v_base_url text;
  v_base_free_shipping boolean;
  v_base_condition_id int;
  v_base_item_count int;
  v_original_deal_product_id int;

  v_existing_id int;
  v_price numeric;
  v_total_discount numeric;

  r_stacked record;
  r_direct record;
BEGIN
  IF p_stacked_deal_id IS NULL AND p_direct_deal_id IS NULL THEN
    RETURN 0;
  END IF;

  -- Direct-deal mode: validate and capture direct store.
  IF p_direct_deal_id IS NOT NULL THEN
    SELECT d.store_id
      INTO v_direct_store_id
    FROM deal d
    WHERE d.id = p_direct_deal_id
      AND d.deal_type_id = 1
      AND d.deleted = false
      AND d.deal_status_id = 2;

    IF v_direct_store_id IS NULL THEN
      RETURN 0;
    END IF;
  END IF;

  FOR r_stacked IN
    SELECT d.id
    FROM deal d
    WHERE d.deal_type_id = 3
      AND d.deleted = false
      AND d.deal_status_id = 2
      AND (p_stacked_deal_id IS NULL OR d.id = p_stacked_deal_id)
  LOOP
    -- Determine stacked store by component deals; all components must share one non-null store.
    SELECT COUNT(DISTINCT c.store_id), MIN(c.store_id)
      INTO v_store_count, v_store_id
    FROM deal_combo dc
    JOIN deal c ON c.id = dc.combo_deal_id
    WHERE dc.deal_id = r_stacked.id
      AND c.deleted = false;

    IF v_store_count IS DISTINCT FROM 1 OR v_store_id IS NULL THEN
      CONTINUE;
    END IF;

    -- In direct mode, only process stacks on that same store.
    IF p_direct_deal_id IS NOT NULL AND v_store_id IS DISTINCT FROM v_direct_store_id THEN
      CONTINUE;
    END IF;

    -- Eligibility gate: stack must include ALL approved active store-wide coupon/external deals on this store.
    SELECT COUNT(*)
      INTO v_sw_total
    FROM deal sw
    WHERE sw.store_id = v_store_id
      AND sw.store_wide = true
      AND sw.deleted = false
      AND sw.deal_status_id = 2
      AND sw.deal_type_id IN (2,4);

    IF v_sw_total > 0 THEN
      SELECT COUNT(DISTINCT sw.id)
        INTO v_sw_in_stack
      FROM deal sw
      JOIN deal_combo dc ON dc.combo_deal_id = sw.id
      WHERE dc.deal_id = r_stacked.id
        AND sw.store_id = v_store_id
        AND sw.store_wide = true
        AND sw.deleted = false
        AND sw.deal_status_id = 2
        AND sw.deal_type_id IN (2,4);

      IF v_sw_in_stack <> v_sw_total THEN
        CONTINUE;
      END IF;
    END IF;

    -- Sum all non-direct component discount percentages.
    SELECT COALESCE(SUM(COALESCE(c.discount_percent, 0)), 0)
      INTO v_total_discount
    FROM deal_combo dc
    JOIN deal c ON c.id = dc.combo_deal_id
    WHERE dc.deal_id = r_stacked.id
      AND c.deleted = false
      AND c.deal_status_id = 2
      AND COALESCE(c.deal_type_id, 0) <> 1;

    FOR r_direct IN
      SELECT d.id
      FROM deal d
      WHERE d.deal_type_id = 1
        AND d.deleted = false
        AND d.deal_status_id = 2
        AND d.store_id = v_store_id
        AND (p_direct_deal_id IS NULL OR d.id = p_direct_deal_id)
    LOOP
      -- Canonical base direct deal_product row to derive from.
      SELECT dp.id,
             dp.product_id,
             dp.product_variant_id,
             dp.price,
             dp.url,
             dp.free_shipping,
             dp.condition_id,
             dp.item_count
        INTO v_original_deal_product_id,
             v_base_product_id,
             v_base_variant_id,
             v_base_price,
             v_base_url,
             v_base_free_shipping,
             v_base_condition_id,
             v_base_item_count
      FROM deal_product dp
      WHERE dp.deal_id = r_direct.id
        AND dp.deleted = false
        AND dp.deal_status_id = 2
        AND dp.url IS NOT NULL
        AND btrim(dp.url) <> ''
        AND dp."primary" = true
      ORDER BY dp.id ASC
      LIMIT 1;

      IF v_original_deal_product_id IS NULL THEN
        CONTINUE;
      END IF;

      -- Apply summed discount to the base direct price.
      v_price := CASE
        WHEN v_total_discount <= 0 THEN v_base_price
        WHEN v_total_discount >= 100 THEN 0
        ELSE round(v_base_price * (1 - (v_total_discount / 100)), 2)
      END;

      -- Find canonical derived row (adopt legacy rows where original_deal_product_id was null).
      SELECT MIN(dp.id)
        INTO v_existing_id
      FROM deal_product dp
      WHERE dp.deal_id = r_stacked.id
        AND (
          dp.original_deal_product_id = v_original_deal_product_id
          OR (
            dp.original_deal_product_id IS NULL
            AND dp.product_id = v_base_product_id
            AND dp.url IS NOT DISTINCT FROM v_base_url
            AND COALESCE(dp."primary", false) = false
          )
        );

      IF v_existing_id IS NOT NULL THEN
        -- Remove duplicates beyond canonical row.
        UPDATE deal_product
           SET deleted = true,
               deal_status_id = 4
         WHERE deal_id = r_stacked.id
           AND (
             original_deal_product_id = v_original_deal_product_id
             OR (
               original_deal_product_id IS NULL
               AND product_id = v_base_product_id
               AND url IS NOT DISTINCT FROM v_base_url
               AND COALESCE("primary", false) = false
             )
           )
           AND id <> v_existing_id
           AND deleted = false;

        UPDATE deal_product
           SET product_id = v_base_product_id,
               product_variant_id = v_base_variant_id,
               price = v_price,
               url = v_base_url,
               free_shipping = v_base_free_shipping,
               condition_id = v_base_condition_id,
               deleted = false,
               deal_status_id = 2,
               "primary" = false,
               original_deal_product_id = v_original_deal_product_id,
               item_count = v_base_item_count
         WHERE id = v_existing_id;

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        v_count := v_count + v_rows;
      ELSE
        INSERT INTO deal_product (
          created_at,
          product_id,
          product_variant_id,
          price,
          url,
          deleted,
          deal_id,
          original_deal_product_id,
          deal_status_id,
          condition_id,
          free_shipping,
          "primary",
          item_count
        )
        VALUES (
          now(),
          v_base_product_id,
          v_base_variant_id,
          v_price,
          v_base_url,
          false,
          r_stacked.id,
          v_original_deal_product_id,
          2,
          v_base_condition_id,
          v_base_free_shipping,
          false,
          v_base_item_count
        );

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        v_count := v_count + v_rows;
      END IF;
    END LOOP;
  END LOOP;

  RETURN v_count;
END;
$$;

GRANT EXECUTE ON FUNCTION public.f_upsert_stacked_deal_products_for_store(bigint, bigint) TO authenticated;
