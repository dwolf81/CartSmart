using System;

namespace CartSmart.API.Models.DTOs
{
    public class StoreWideDealDTO
    {
        public int StoreId { get; set; }
        public int DealTypeId { get; set; }
        public string? AdditionalDetails { get; set; }
        public string? CouponCode { get; set; }
        public int? DiscountPercent { get; set; }
        public DateTime? ExpirationDate { get; set; }
        public string? ExternalOfferUrl { get; set; }
    }
}
