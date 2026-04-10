-- Fix: skip creating derived deal_product rows for store-wide coupon/external
-- deals when their external_offer_url already matches the direct deal's URL.
-- This prevents duplicate "variant" rows on the product page when a coupon deal
-- and a direct deal point to the same product listing.

CREATE OR REPLACE FUNCTION public.f_upsert_storewide_deal_products_for_direct_deal(
  p_direct_deal_id bigint
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
  v_base_item_count int;
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
  SELECT dp.id,
         dp.product_id,
         dp.product_variant_id,
         dp.price,
         dp.url,
         dp.free_shipping,
         dp.condition_id,
         dp.item_count
    INTO v_original_deal_product_id,
         v_product_id,
         v_product_variant_id,
         v_base_price,
         v_base_url,
         v_base_free_shipping,
         v_base_condition_id,
         v_base_item_count
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

  -- Iterate store-wide coupon/external deals on the same store (approved only).
  FOR r_sw IN
    SELECT id, discount_percent
      FROM deal
     WHERE store_id = v_store_id
       AND store_wide = true
       AND deleted = false
       AND deal_status_id = 2
       AND deal_type_id IN (2,4)
  LOOP
    -- Skip if this store-wide deal already has a non-derived deal_product
    -- with the same URL (e.g. manually entered coupon deal for this listing).
    IF EXISTS (
      SELECT 1
      FROM deal_product dp
      WHERE dp.deal_id = r_sw.id
        AND dp.deleted = false
        AND dp.original_deal_product_id IS NULL
        AND btrim(dp.url) = btrim(v_base_url)
    ) THEN
      CONTINUE;
    END IF;

    v_price := CASE
      WHEN r_sw.discount_percent IS NULL OR r_sw.discount_percent <= 0 THEN v_base_price
      WHEN r_sw.discount_percent >= 100 THEN 0
      ELSE round(v_base_price * (1 - (r_sw.discount_percent::numeric / 100)), 2)
    END;

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
             product_id = v_product_id,
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
        false,
        v_base_item_count
      );

      GET DIAGNOSTICS v_rows = ROW_COUNT;
      v_count := v_count + v_rows;
    END IF;
  END LOOP;

  RETURN v_count;
END;
$$;

GRANT EXECUTE ON FUNCTION public.f_upsert_storewide_deal_products_for_direct_deal(bigint) TO authenticated;
