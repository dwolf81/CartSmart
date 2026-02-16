-- Adds triggers to keep derived store-wide deal_product rows in sync with their originating
-- direct deal_product row (via original_deal_product_id).
--
-- Behavior:
-- - When an original (direct) deal_product is edited (price/url/shipping/condition/variant),
--   derived rows update their pricing (store-wide % off) and other fields.
-- - When an original deal_product is soft-deleted or becomes non-approved, derived rows are soft-deleted.
-- - When the parent direct deal is soft-deleted (deal.deleted = true) or made non-approved,
--   derived rows are soft-deleted as well.

ALTER TABLE public.deal_product
  ADD COLUMN IF NOT EXISTS original_deal_product_id int;

CREATE INDEX IF NOT EXISTS idx_deal_product_original_deal_product_id
  ON public.deal_product(original_deal_product_id);

CREATE INDEX IF NOT EXISTS idx_deal_product_deal_id_original_deal_product_id
  ON public.deal_product(deal_id, original_deal_product_id);


CREATE OR REPLACE FUNCTION public.f_sync_storewide_deal_products_from_original_deal_product()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
  -- Only react to ORIGINAL rows (derived rows have original_deal_product_id set).
  IF NEW.original_deal_product_id IS NOT NULL THEN
    RETURN NEW;
  END IF;

  -- If the original row is no longer active/approved or has no URL, soft-delete derived rows.
  IF NEW.deleted IS TRUE
     OR NEW.deal_status_id IS DISTINCT FROM 2
     OR NEW.url IS NULL
     OR btrim(NEW.url) = '' THEN

    UPDATE public.deal_product dp
       SET deleted = true,
           deal_status_id = 4
     WHERE dp.original_deal_product_id = NEW.id
       AND dp.deleted = false;

    RETURN NEW;
  END IF;

  -- Update derived rows (store-wide coupon/external deals) to match the original row.
  UPDATE public.deal_product dp
     SET price = CASE
                  WHEN d.discount_percent IS NULL OR d.discount_percent <= 0 THEN NEW.price
                  WHEN d.discount_percent >= 100 THEN 0
                  ELSE round(NEW.price * (1 - (d.discount_percent::numeric / 100)), 2)
                END,
         url = NEW.url,
         free_shipping = NEW.free_shipping,
         condition_id = NEW.condition_id,
         product_variant_id = NEW.product_variant_id,
         product_id = NEW.product_id,
         deleted = false,
         deal_status_id = 2,
         "primary" = false
    FROM public.deal d
   WHERE dp.deal_id = d.id
     AND dp.original_deal_product_id = NEW.id
     AND d.store_wide = true
     AND d.deleted = false
     AND d.deal_status_id = 2
     AND d.deal_type_id IN (2,4);

  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_sync_storewide_from_original_deal_product ON public.deal_product;
CREATE TRIGGER trg_sync_storewide_from_original_deal_product
AFTER UPDATE OF price, url, free_shipping, condition_id, product_variant_id, product_id, deleted, deal_status_id
ON public.deal_product
FOR EACH ROW
EXECUTE FUNCTION public.f_sync_storewide_deal_products_from_original_deal_product();


CREATE OR REPLACE FUNCTION public.f_sync_storewide_deal_products_from_original_deal()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
  -- Only applies to direct (non-store-wide) deals.
  IF NEW.deal_type_id IS DISTINCT FROM 1 OR NEW.store_wide IS TRUE THEN
    RETURN NEW;
  END IF;

  -- If deal is soft-deleted or not approved, soft-delete derived rows.
  IF NEW.deleted IS TRUE OR NEW.deal_status_id IS DISTINCT FROM 2 THEN
    UPDATE public.deal_product dp
       SET deleted = true,
           deal_status_id = 4
     WHERE dp.original_deal_product_id IN (
       SELECT id FROM public.deal_product
        WHERE deal_id = NEW.id
     )
       AND dp.deleted = false;

    RETURN NEW;
  END IF;

  -- If the deal becomes approved again, re-apply derived rows.
  PERFORM public.f_upsert_storewide_deal_products_for_direct_deal(NEW.id);

  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_sync_storewide_from_original_deal ON public.deal;
CREATE TRIGGER trg_sync_storewide_from_original_deal
AFTER UPDATE OF deleted, deal_status_id
ON public.deal
FOR EACH ROW
EXECUTE FUNCTION public.f_sync_storewide_deal_products_from_original_deal();
