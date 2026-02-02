using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace CartSmart.API.Models.DTOs
{
    public class ProductDTO
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Slug { get; set; }
        public float? MSRP { get; set; }
        public float? LowPrice { get; set; }
        public int BrandId { get; set; }
        public int UserId { get; set; }
        public int? DealId { get; set; }

        public int? Rating { get; set; }

        public string? ImageUrl { get; set; }

        public string? BrandName { get; set; }
    }

    public class DealDisplayDTO
    {
        public string url { get; set; }
        public string additional_details { get; set; }
        public string condition_name { get; set; }
        public long? condition_id { get; set; }
        public float? price { get; set; }
        public int? level { get; set; } // User level
        public string user_name { get; set; } // User name
        public string display_name { get; set; } // Display name
        public string slug { get; set; } // Product slug
        public string product_name { get; set; } // Product name
        public long deal_id { get; set; } // Deal ID
        public long? product_id { get; set; } // Product ID (null for store-wide deals)
        public long? store_id { get; set; } // Store ID
        public string? store_name { get; set; } // Store name
        public string? store_image_url { get; set; } // Store logo
        public string? store_slug { get; set; } // Store slug
        public long? deal_product_id { get; set; } // Deal Product ID (null for store-wide deals)
        public int? product_variant_id { get; set; } // Product Variant ID (enriched from deal_product)
        public float? discount_amt { get; set; } // Calculated discount amount
        public float? discount_percent { get; set; } // Calculated discount percent
        public string? product_image_url { get; set; } // Product name
        public float? msrp { get; set; } // msrp amount
        public string brand_name { get; set; } // Brand name
        public string? user_image_url { get; set; } // User image URL
        public string? deal_type_name { get; set; } // deal type name
        public string? coupon_code { get; set; } // deal type name
        public bool? free_shipping { get; set; } // Free Shipping
        public int? deal_status_id { get; set; }
        public string deal_status_name { get; set; } // deal status name
        public int? deal_type_id { get; set; }
        public DateTime created_at { get; set; }
        public DateTime? expiration_date { get; set; }
        public bool user_flagged { get; set; }  // default false if anonymous
        public string? review_comment { get; set; } // Review comment left by reviewer
        public int? review_deal_status_id { get; set; } // Review deal status left by reviewer        
        public string? store_url { get; set; } // store url
        public string? external_offer_store_url { get; set; } // store url
        public string? external_offer_url { get; set; }
        public float? upfront_cost { get; set; }
        public int? upfront_cost_term_id { get; set; }
        public string? external_store_url { get; set; } // external store url
        public float? external_upfront_cost { get; set; }
        public int? external_upfront_cost_term_id { get; set; }
        public long? parent_deal_id { get; set; } // Deal ID for the parent store record
        public List<DealDisplayDTO> steps { get; set; } = new List<DealDisplayDTO>();
        public List<ReviewDisplayDTO> reviews { get; set; } = new List<ReviewDisplayDTO>();
        public int? store_deal_count { get; set; }
        public int? additional_deal_count { get; set; }

        public string? affiliate_code { get; set; } // store affiliate code
        public string? affiliate_code_var { get; set; }

        public string? external_affiliate_code { get; set; } // store affiliate code
        public string? external_affiliate_code_var { get; set; }
    }

    // Used by /categories/{productType} to show all products in that product type.
    // Fields intentionally match the existing deal-card shape used on the Home page.
    public class CategoryProductCardDTO
    {
        public long product_id { get; set; }
        public string? product_name { get; set; }
        public string? slug { get; set; }
        public string? description { get; set; }
        public string? product_image_url { get; set; }
        public float? msrp { get; set; }
        public float? price { get; set; }
        public float? discount_amt { get; set; }

        // Optional best-deal enrichment (may be null if the product has no deals)
        public long? deal_id { get; set; }
        public long? store_id { get; set; }
        public string? user_name { get; set; }
        public string? user_image_url { get; set; }
        public int? level { get; set; }
    }

        public class ReviewDisplayDTO
    {
        public string? review_comment { get; set; } // Review comment left by reviewer
        public int? review_deal_status_id { get; set; } // Review deal status left by reviewer   
    }
/*
    public class CreateDealDTO
    {
        public int ProductId { get; set; }
        public int DealTypeId { get; set; }
        public float Price { get; set; }
        public bool FreeShipping { get; set; }
        public string Url { get; set; }
        public string additionalDetails { get; set; }
        public string? CouponCode { get; set; }
        public int ConditionId { get; set; }
        public string? ExternalOfferUrl { get; set; }
        public int? DiscountPercent { get; set; }
        public float? UpfrontCost { get; set; }
        public int? UpfrontCostTermId { get; set; }
        public List<int> DealIds { get; set; } = new List<int>();
    }
*/
    public class DealProductDTO
    {
        public int DealProductId { get; set; }
        public int ProductId { get; set; }              // which product row to update
        public int DealId { get; set; }                 // which deal row to update
        public int DealTypeId { get; set; }             // shared on Deal
        public decimal? Price { get; set; }               // DealProduct
        public bool FreeShipping { get; set; }          // DealProduct
        public string? Url { get; set; }                // DealProduct
        public string? AdditionalDetails { get; set; }  // Deal
        public string? CouponCode { get; set; }         // Deal (type=2)
        public int? ConditionId { get; set; }           // DealProduct
        public int? DiscountPercent { get; set; }       // Deal
        public DateTime? ExpirationDate { get; set; }   // Deal
        public string? ExternalOfferUrl { get; set; }   // Deal (type=4)
        public float? UpfrontCost { get; set; }
        public int? UpfrontCostTermId { get; set; }
        public List<int> DealIds { get; set; } = new List<int>();  // List of existing Deal IDs to link to (for type=3)

        // Optional: selected product attributes (enum values) used to resolve/create product_variant(s).
        // Multiple enumValueIds per attribute means "this URL covers multiple options".
        [JsonProperty("variantAttributes")]
        [JsonPropertyName("variantAttributes")]
        public List<DealVariantAttributeSelectionDTO>? VariantAttributes { get; set; }
    }

    public class DealVariantAttributeSelectionDTO
    {
        [JsonProperty("attributeId")]
        [JsonPropertyName("attributeId")]
        public int AttributeId { get; set; }

        [JsonProperty("enumValueIds")]
        [JsonPropertyName("enumValueIds")]
        public List<int> EnumValueIds { get; set; } = new();
    }

    public class PagedDealsResultDTO<T>
    {
        public List<T> Deals { get; set; } = new List<T>();
        public int TotalCount { get; set; }
    }
}