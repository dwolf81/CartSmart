-- Marks active deal_products as sold when they are no longer found in search
-- results during ingest for a given product and store. Uses last_checked_at vs
-- the ingest start timestamp to determine staleness, avoiding the need to fetch
-- all rows client-side.
--
-- Also cascades: if a parent deal has no remaining active deal_products after
-- the update, it is marked as sold too.
--
-- Returns the number of deal_products marked sold.

create or replace function f_mark_stale_deal_products_sold(
  p_product_id bigint,
  p_store_id integer,
  p_ingest_started_at timestamptz
)
returns integer
language plpgsql
as $$
declare
  v_affected_count integer;
  v_deal_id bigint;
begin
  -- Mark active deal_products as sold if they belong to a deal for the
  -- specified store and were not refreshed during this ingest run.
  -- Exclude rows created after the ingest started (newly inserted this run).
  with updated as (
    update deal_product dp
    set deal_status_id = 7  -- Sold
    from deal d
    where dp.deal_id = d.id
      and dp.product_id = p_product_id
      and d.store_id = p_store_id
      and dp.deleted = false
      and dp.deal_status_id = 2  -- Active
      and dp.created_at < p_ingest_started_at
      and (dp.last_checked_at is null or dp.last_checked_at < p_ingest_started_at)
    returning dp.id, dp.deal_id
  )
  select count(*)::integer into v_affected_count from updated;

  -- Cascade: mark parent deals as sold if they have no remaining active deal_products.
  for v_deal_id in
    select distinct dp.deal_id
    from deal_product dp
    inner join deal d on d.id = dp.deal_id
    where dp.product_id = p_product_id
      and d.store_id = p_store_id
      and dp.deleted = false
      and dp.deal_status_id = 7
      and d.deleted = false
      and d.deal_status_id = 2  -- parent still Active
      and not exists (
        select 1 from deal_product sibling
        where sibling.deal_id = dp.deal_id
          and sibling.deal_status_id = 2  -- any active sibling remains
      )
  loop
    update deal
    set deal_status_id = 7
    where id = v_deal_id
      and deleted = false
      and deal_status_id = 2;
  end loop;

  return v_affected_count;
end;
$$;
