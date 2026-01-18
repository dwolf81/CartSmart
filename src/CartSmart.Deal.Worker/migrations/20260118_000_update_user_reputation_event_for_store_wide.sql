-- Enable user_reputation_event to track store-wide deals (which have no deal_product).
-- Store-wide reputation events will be keyed by deal_id.

alter table user_reputation_event
  add column if not exists deal_id int null;

-- Store-wide events have no deal_product_id
alter table user_reputation_event
  alter column deal_product_id drop not null;

-- Keep lookups fast
create index if not exists idx_user_reputation_event_deal_id
  on user_reputation_event(deal_id);

create index if not exists idx_user_reputation_event_deal_product_id
  on user_reputation_event(deal_product_id);

-- Ensure at least one reference exists
alter table user_reputation_event
  drop constraint if exists user_reputation_event_deal_ref_chk;

alter table user_reputation_event
  add constraint user_reputation_event_deal_ref_chk
  check (deal_product_id is not null or deal_id is not null);
