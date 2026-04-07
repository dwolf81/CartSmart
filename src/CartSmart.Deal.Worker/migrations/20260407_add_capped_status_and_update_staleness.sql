-- Add "Capped" deal status (id=9) for deal_products that are still live on the
-- marketplace but exceed the per-condition/variant ingest cap.
insert into deal_status (id, name)
values (9, 'Capped')
on conflict (id) do nothing;

-- Update the staleness function to also mark Capped (9) deal_products as Sold
-- when they are no longer found in search results (i.e. last_checked_at is stale).
-- Previously it only targeted Active (2).
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
  -- Mark Active (2) or Capped (9) deal_products as Sold (7) if they belong to
  -- a deal for the specified store and were not refreshed during this ingest run.
  -- Exclude rows created after the ingest started (newly inserted this run).
  with updated as (
    update deal_product dp
    set deal_status_id = 7  -- Sold
    from deal d
    where dp.deal_id = d.id
      and dp.product_id = p_product_id
      and d.store_id = p_store_id
      and dp.deleted = false
      and dp.deal_status_id in (2, 9)  -- Active or Capped
      and dp.created_at < p_ingest_started_at
      and (dp.last_checked_at is null or dp.last_checked_at < p_ingest_started_at)
    returning dp.id, dp.deal_id
  )
  select count(*)::integer into v_affected_count from updated;

  -- Cascade: mark parent deals as sold if they have no remaining active/capped deal_products.
  for v_deal_id in
    select distinct dp.deal_id
    from deal_product dp
    inner join deal d on d.id = dp.deal_id
    where dp.product_id = p_product_id
      and d.store_id = p_store_id
      and dp.deleted = false
      and dp.deal_status_id = 7
      and d.deleted = false
      and d.deal_status_id in (2, 9)  -- parent still Active or Capped
      and not exists (
        select 1 from deal_product sibling
        where sibling.deal_id = dp.deal_id
          and sibling.deal_status_id in (2, 9)  -- any active/capped sibling remains
      )
  loop
    update deal
    set deal_status_id = 7
    where id = v_deal_id
      and deleted = false
      and deal_status_id in (2, 9);
  end loop;

  return v_affected_count;
end;
$$;
