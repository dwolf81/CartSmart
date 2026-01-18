-- Store-wide deals have no deal_product row, so deal_review.deal_product_id must be nullable.

alter table deal_review
  alter column deal_product_id drop not null;
