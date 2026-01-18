using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("deal")]
    public class Deal : BaseModel
    {
        [PrimaryKey("id")]
        [JsonIgnore]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("coupon_code")]
        public string? CouponCode { get; set; }

        [Column("additional_details")]
        public string? AdditionalDetails { get; set; }

        [Column("deal_status_id")]
        public int DealStatusId { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Column("deleted")]
        public bool Deleted { get; set; }

        [Column("store_id")]
        public int? StoreId { get; set; }

        [Column("discount_percent")]
        public int? DiscountPercent { get; set; }

        [Column("deal_type_id")]
        public int? DealTypeId { get; set; }

        [Column("parent_deal_id")]
        public int? ParentDealId { get; set; }

        [Column("external_offer_url")]
        public string? ExternalOfferUrl { get; set; }

        [Column("external_offer_store_id")]
        public int? ExternalOfferStoreId { get; set; }

        [Column("expiration_date")]
        public DateTime? ExpirationDate { get; set; }

        [Column("store_wide")]
        public bool StoreWide { get; set; }        


    }
}