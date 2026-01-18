namespace CartSmart.API.Models.DTOs
{
    public class StoreSummaryDTO
    {
        public int id { get; set; }
        public string? name { get; set; }
        public string? url { get; set; }
        public string? slug { get; set; }
        public string? imageUrl { get; set; }
    }

    public class StorePageResponseDTO
    {
        public StoreSummaryDTO? store { get; set; }
        public List<DealDisplayDTO> storeDeals { get; set; } = new();
        public List<CategoryProductCardDTO> products { get; set; } = new();
    }

    public class StoreDealCardDTO
    {
        public long deal_id { get; set; }
        public string? additional_details { get; set; }
        public int? discount_percent { get; set; }
        public int? level { get; set; }
        public string? user_name { get; set; }
        public string? user_image_url { get; set; }
        public decimal? upfront_cost { get; set; }
        public short? upfront_cost_term_id { get; set; }

        // Enriched fields (not returned by f_store_deals today, but populated server-side)
        public int? deal_type_id { get; set; }
        public string? coupon_code { get; set; }
        public string? url { get; set; }
        public string? store_url { get; set; }
        public string? external_offer_url { get; set; }
        public string? external_store_url { get; set; }
        public decimal? external_upfront_cost { get; set; }
        public short? external_upfront_cost_term_id { get; set; }

        // Needed for existing /api/deals/{id}/flag endpoint
        public long? deal_product_id { get; set; }
    }
}
