-- Adds a per-product toggle to control whether background service tasks (refresh/ingest)
-- should run for a product.

alter table public.product
  add column if not exists enable_service boolean;

-- Backfill existing rows (if any were created before this column existed).
update public.product
set enable_service = true
where enable_service is null;

-- Enforce non-null + default for future rows.
alter table public.product
  alter column enable_service set default true;

alter table public.product
  alter column enable_service set not null;

-- Optional: helps worker queries that filter by enable_service.
create index if not exists idx_product_enable_service
  on public.product (enable_service)
  where deleted = false;
