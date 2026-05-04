-- Social media post tables for automated golf deal posting

create table if not exists social_post (
    id              bigserial       primary key,
    created_at      timestamptz     not null default now(),
    deal_id         int             not null,
    product_id      int             not null,
    product_name    text            not null,
    product_image   text,
    current_price   numeric(10,2)   not null,
    original_price  numeric(10,2),
    deal_url        text            not null,
    -- pending_approval | approved | posted | rejected
    status          text            not null default 'pending_approval',
    scheduled_date  date,
    posted_at       timestamptz,
    is_weekly       bool            not null default false,
    image_url       text,
    admin_notes     text
);

create index if not exists idx_social_post_status
    on social_post(status);

create index if not exists idx_social_post_scheduled_date
    on social_post(scheduled_date desc);

create index if not exists idx_social_post_created_at
    on social_post(created_at desc);

-- Prevent duplicate posts for the same deal on the same day (unless rejected)
create unique index if not exists ux_social_post_deal_date
    on social_post(deal_id, scheduled_date)
    where status != 'rejected';

create table if not exists social_post_caption (
    id              bigserial   primary key,
    created_at      timestamptz not null default now(),
    social_post_id  bigint      not null references social_post(id) on delete cascade,
    caption_text    text        not null,
    -- all | twitter | facebook | instagram
    platform        text        not null default 'all',
    selected        bool        not null default false
);

create index if not exists idx_social_post_caption_post_id
    on social_post_caption(social_post_id);
