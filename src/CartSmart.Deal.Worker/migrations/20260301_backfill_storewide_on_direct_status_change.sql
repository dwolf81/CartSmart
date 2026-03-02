-- Ensure direct deal status transitions backfill store-wide derived rows.
--
-- Why:
--   Some approval/update paths transition deal.deal_status_id to approved without
--   invoking f_upsert_storewide_deal_products_for_direct_deal directly.
--   The stacked sync trigger already runs on direct status updates, so we also invoke
--   store-wide upsert there to keep store-wide coupon/external derived rows in sync.

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
