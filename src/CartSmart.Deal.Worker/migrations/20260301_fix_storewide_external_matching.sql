-- Fix store-wide propagation for direct deals.
--
-- Root cause:
--   The database had f_upsert_storewide_deal_products_for_direct_deal(bigint)
--   but GRANTs were only on the (int) overload.  PostgREST could not resolve
--   the function via RPC, so the C# call silently failed.
--
-- Fix:
--   1. Drop the stale (bigint) overload so there is exactly one function.
--   2. Re-create with (int) parameter matching all call-sites and GRANTs.

-- Remove the stale bigint overload if it exists
DROP FUNCTION IF EXISTS public.f_upsert_storewide_deal_products_for_direct_deal(bigint);

CREATE OR REPLACE FUNCTION public.f_upsert_storewide_deal_products_for_direct_deal(
  p_direct_deal_id int
)
RETURNS int
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_store_id int;
  v_deal_type_id int;
  v_deal_status_id int;
  v_submitter_admin boolean;

  v_product_id int;
  v_product_variant_id bigint;
  v_base_price numeric;
  v_base_url text;
  v_base_free_shipping boolean;
  v_base_condition_id int;
  v_original_deal_product_id int;

  v_existing_id int;
  v_rows int := 0;
  v_count int := 0;
  v_price numeric;

  r_sw record;
BEGIN
  -- Load direct deal context
  SELECT d.store_id, d.deal_type_id, d.deal_status_id, u.admin
    INTO v_store_id, v_deal_type_id, v_deal_status_id, v_submitter_admin
  FROM deal d
  JOIN "user" u ON u.id = d.user_id
  WHERE d.id = p_direct_deal_id;

  -- Must exist and must be a direct deal
  IF v_store_id IS NULL OR v_deal_type_id IS DISTINCT FROM 1 THEN
    RETURN 0;
  END IF;

  -- Gate: approved OR submitter is admin
  IF NOT (v_deal_status_id = 2 OR v_submitter_admin IS TRUE) THEN
    RETURN 0;
  END IF;

  -- Pick ONE base deal_product row to propagate (primary first).
  -- This prevents multiplying rows when a direct deal has multiple variant rows.
  SELECT dp.id,
         dp.product_id,
         dp.product_variant_id,
         dp.price,
         dp.url,
         dp.free_shipping,
         dp.condition_id
    INTO v_original_deal_product_id,
         v_product_id,
         v_product_variant_id,
         v_base_price,
         v_base_url,
         v_base_free_shipping,
         v_base_condition_id
  FROM deal_product dp
  WHERE dp.deal_id = p_direct_deal_id
    AND dp.deleted = false
    AND dp.deal_status_id = 2
    AND dp.url IS NOT NULL
    AND btrim(dp.url) <> ''
    AND dp."primary" = true
  ORDER BY dp.id ASC
  LIMIT 1;

  IF v_product_id IS NULL THEN
    RETURN 0;
  END IF;

  -- Iterate store-wide coupon/external deals on the same store (approved only)
  FOR r_sw IN
    SELECT id, discount_percent
      FROM deal
     WHERE store_id = v_store_id
       AND store_wide = true
       AND deleted = false
       AND deal_status_id = 2
       AND deal_type_id IN (2,4)
  LOOP
    v_price := CASE
      WHEN r_sw.discount_percent IS NULL OR r_sw.discount_percent <= 0 THEN v_base_price
      WHEN r_sw.discount_percent >= 100 THEN 0
      ELSE round(v_base_price * (1 - (r_sw.discount_percent::numeric / 100)), 2)
    END;

    -- Ensure only ONE derived deal_product exists per (store-wide deal, original_deal_product).
    -- Also adopt legacy rows that were created before original_deal_product_id existed.
    SELECT MIN(dp.id)
      INTO v_existing_id
    FROM deal_product dp
    WHERE dp.deal_id = r_sw.id
      AND (
        dp.original_deal_product_id = v_original_deal_product_id
        OR (
          dp.original_deal_product_id IS NULL
          AND dp.product_id = v_product_id
          AND dp.url IS NOT DISTINCT FROM v_base_url
        )
      );

    IF v_existing_id IS NOT NULL THEN
      -- Soft-delete any duplicates beyond the canonical row.
      UPDATE deal_product
         SET deleted = true
       WHERE deal_id = r_sw.id
         AND (
           original_deal_product_id = v_original_deal_product_id
           OR (
             original_deal_product_id IS NULL
             AND product_id = v_product_id
             AND url IS NOT DISTINCT FROM v_base_url
           )
         )
         AND id <> v_existing_id
         AND deleted = false;

      UPDATE deal_product
         SET price = v_price,
             url = v_base_url,
             free_shipping = v_base_free_shipping,
             condition_id = v_base_condition_id,
             product_variant_id = v_product_variant_id,
             "primary" = false,
             deal_status_id = 2,
             deleted = false,
             original_deal_product_id = v_original_deal_product_id,
             product_id = v_product_id
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
        "primary"
      )
      VALUES (
        now(),
        v_product_id,
        v_product_variant_id,
        v_price,
        v_base_url,
        false,
        r_sw.id,
        v_original_deal_product_id,
        2,
        v_base_condition_id,
        v_base_free_shipping,
        false
      );

      GET DIAGNOSTICS v_rows = ROW_COUNT;
      v_count := v_count + v_rows;
    END IF;
  END LOOP;

  RETURN v_count;
END;
$$;

GRANT EXECUTE ON FUNCTION public.f_upsert_storewide_deal_products_for_direct_deal(int) TO authenticated;
