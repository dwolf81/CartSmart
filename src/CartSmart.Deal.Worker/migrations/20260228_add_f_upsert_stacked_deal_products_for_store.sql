-- Applies approved stacked deals (that include all approved store-wide deals for a store)
-- across direct deals for that store.
--
-- Supports two modes:
-- 1) p_direct_deal_id provided: apply all eligible stacked deals on that store to that one direct deal.
-- 2) p_stacked_deal_id provided: apply that stacked deal to all approved direct deals on its store.

CREATE OR REPLACE FUNCTION public.f_upsert_stacked_deal_products_for_store(
  p_stacked_deal_id int DEFAULT NULL,
  p_direct_deal_id int DEFAULT NULL
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
  v_original_deal_product_id int;

  v_existing_id int;
  v_price numeric;

  r_stacked record;
  r_direct record;
  r_comp record;
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
             dp.condition_id
        INTO v_original_deal_product_id,
             v_base_product_id,
             v_base_variant_id,
             v_base_price,
             v_base_url,
             v_base_free_shipping,
             v_base_condition_id
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

      -- Apply stacked component discounts to the base direct price.
      v_price := v_base_price;
      FOR r_comp IN
        SELECT c.deal_type_id, COALESCE(c.discount_percent, 0) AS discount_percent
        FROM deal_combo dc
        JOIN deal c ON c.id = dc.combo_deal_id
        WHERE dc.deal_id = r_stacked.id
          AND c.deleted = false
          AND c.deal_status_id = 2
        ORDER BY dc."order" ASC
      LOOP
        -- Skip direct component(s); base price already reflects direct deal.
        IF COALESCE(r_comp.deal_type_id, 0) = 1 THEN
          CONTINUE;
        END IF;

        IF r_comp.discount_percent >= 100 THEN
          v_price := 0;
          EXIT;
        ELSIF r_comp.discount_percent > 0 THEN
          v_price := round(v_price * (1 - (r_comp.discount_percent::numeric / 100)), 2);
        END IF;
      END LOOP;

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
               original_deal_product_id = v_original_deal_product_id
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
          false
        );

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        v_count := v_count + v_rows;
      END IF;
    END LOOP;
  END LOOP;

  RETURN v_count;
END;
$$;

GRANT EXECUTE ON FUNCTION public.f_upsert_stacked_deal_products_for_store(int, int) TO authenticated;


CREATE OR REPLACE FUNCTION public.f_sync_stacked_deal_products_from_deal()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
  -- Direct deal path
  IF NEW.deal_type_id = 1 THEN
    -- If direct deal becomes inactive, soft-delete derived stacked rows sourced from it.
    IF NEW.deleted IS TRUE OR NEW.deal_status_id IS DISTINCT FROM 2 THEN
      UPDATE public.deal_product dp
         SET deleted = true,
             deal_status_id = 4
       WHERE dp.original_deal_product_id IN (
         SELECT id
         FROM public.deal_product
         WHERE deal_id = NEW.id
       )
         AND dp.deal_id IN (
           SELECT d.id FROM public.deal d WHERE d.deal_type_id = 3
         )
         AND dp.deleted = false;
      RETURN NEW;
    END IF;

    -- If direct deal is active, backfill/update eligible store-wide + stacked rows for this direct deal.
    -- This keeps behavior correct even when approval paths update deal status directly.
    PERFORM public.f_upsert_storewide_deal_products_for_direct_deal(NEW.id);
    PERFORM public.f_upsert_stacked_deal_products_for_store(NULL, NEW.id);
    RETURN NEW;
  END IF;

  -- Stacked deal path
  IF NEW.deal_type_id = 3 THEN
    -- If stacked deal becomes inactive, soft-delete only derived rows (keep its original/manual rows).
    IF NEW.deleted IS TRUE OR NEW.deal_status_id IS DISTINCT FROM 2 THEN
      UPDATE public.deal_product
         SET deleted = true,
             deal_status_id = 4
       WHERE deal_id = NEW.id
         AND original_deal_product_id IS NOT NULL
         AND deleted = false;
      RETURN NEW;
    END IF;

    -- If stacked deal is active, apply to all approved direct deals on the same store.
    PERFORM public.f_upsert_stacked_deal_products_for_store(NEW.id, NULL);
    RETURN NEW;
  END IF;

  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_sync_stacked_from_deal ON public.deal;
CREATE TRIGGER trg_sync_stacked_from_deal
AFTER UPDATE OF deleted, deal_status_id
ON public.deal
FOR EACH ROW
EXECUTE FUNCTION public.f_sync_stacked_deal_products_from_deal();


-- Helper: when a store-wide coupon/external deal is approved/inserted,
-- apply it to all approved direct deals for the same store.
CREATE OR REPLACE FUNCTION public.f_upsert_storewide_deal_products_for_storewide_deal(
  p_storewide_deal_id int
)
RETURNS int
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_store_id int;
  v_count int := 0;
  r_direct record;
BEGIN
  SELECT d.store_id
    INTO v_store_id
  FROM deal d
  WHERE d.id = p_storewide_deal_id
    AND d.store_wide = true
    AND d.deal_type_id IN (2,4)
    AND d.deleted = false
    AND d.deal_status_id = 2;

  IF v_store_id IS NULL THEN
    RETURN 0;
  END IF;

  FOR r_direct IN
    SELECT d.id
    FROM deal d
    WHERE d.store_id = v_store_id
      AND d.deal_type_id = 1
      AND d.deleted = false
      AND d.deal_status_id = 2
  LOOP
    v_count := v_count + COALESCE(public.f_upsert_storewide_deal_products_for_direct_deal(r_direct.id), 0);
  END LOOP;

  RETURN v_count;
END;
$$;

GRANT EXECUTE ON FUNCTION public.f_upsert_storewide_deal_products_for_storewide_deal(int) TO authenticated;


CREATE OR REPLACE FUNCTION public.f_sync_storewide_deal_products_from_storewide_deal()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
  IF NEW.store_wide IS NOT TRUE OR NEW.deal_type_id NOT IN (2,4) THEN
    RETURN NEW;
  END IF;

  -- If deactivated, soft-delete derived rows for this store-wide deal.
  IF NEW.deleted IS TRUE OR NEW.deal_status_id IS DISTINCT FROM 2 THEN
    UPDATE public.deal_product
       SET deleted = true,
           deal_status_id = 4
     WHERE deal_id = NEW.id
       AND original_deal_product_id IS NOT NULL
       AND deleted = false;
    RETURN NEW;
  END IF;

  -- If active + approved, backfill/apply across all approved direct deals on the store.
  PERFORM public.f_upsert_storewide_deal_products_for_storewide_deal(NEW.id);
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_sync_storewide_from_storewide_deal_insert ON public.deal;
CREATE TRIGGER trg_sync_storewide_from_storewide_deal_insert
AFTER INSERT
ON public.deal
FOR EACH ROW
EXECUTE FUNCTION public.f_sync_storewide_deal_products_from_storewide_deal();

DROP TRIGGER IF EXISTS trg_sync_storewide_from_storewide_deal_update ON public.deal;
CREATE TRIGGER trg_sync_storewide_from_storewide_deal_update
AFTER UPDATE OF deleted, deal_status_id
ON public.deal
FOR EACH ROW
EXECUTE FUNCTION public.f_sync_storewide_deal_products_from_storewide_deal();
