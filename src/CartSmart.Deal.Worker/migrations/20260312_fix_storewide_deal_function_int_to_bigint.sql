-- Fix: change f_upsert_storewide_deal_products_for_storewide_deal parameter from int to bigint.
-- The trigger f_sync_storewide_deal_products_from_storewide_deal() passes NEW.id (bigint)
-- into this function, but it was defined with an int parameter, causing:
--   ERROR 42883: function public.f_upsert_storewide_deal_products_for_storewide_deal(bigint) does not exist

-- Drop the old int-signature so we don't leave a stale copy behind.
DROP FUNCTION IF EXISTS public.f_upsert_storewide_deal_products_for_storewide_deal(int);

-- Recreate with bigint parameter
CREATE OR REPLACE FUNCTION public.f_upsert_storewide_deal_products_for_storewide_deal(
  p_storewide_deal_id bigint
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

GRANT EXECUTE ON FUNCTION public.f_upsert_storewide_deal_products_for_storewide_deal(bigint) TO authenticated;
GRANT EXECUTE ON FUNCTION public.f_upsert_storewide_deal_products_for_storewide_deal(bigint) TO service_role;
