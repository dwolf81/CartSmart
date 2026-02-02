-- Manual price capture tasks (used when scraping is blocked by bot protection)

create table if not exists manual_price_task (
  id bigserial primary key,
  deal_product_id integer not null references deal_product(id) on delete cascade,
  url text not null,
  reason text not null default 'bot_protection',
  status text not null default 'pending',
  created_at timestamptz not null default now(),
  submitted_at timestamptz null,
  submitted_price numeric null,
  submitted_currency text null,
  submitted_in_stock boolean null,
  submitted_sold boolean null,
  submitted_by text null,
  notes text null
);

create index if not exists ix_manual_price_task_status_created
  on manual_price_task(status, created_at desc);

-- Avoid spamming multiple open tasks for the same deal_product.
create unique index if not exists ux_manual_price_task_one_pending_per_deal_product
  on manual_price_task(deal_product_id)
  where status = 'pending';
