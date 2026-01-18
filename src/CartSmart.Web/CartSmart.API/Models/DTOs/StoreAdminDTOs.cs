namespace CartSmart.API.Models.DTOs
{
    public class AdminStoreDTO
    {
        public int id { get; set; }
        public string? name { get; set; }
        public string? url { get; set; }
        public string? affiliateCode { get; set; }
        public string? affiliateCodeVar { get; set; }
        public int? brandId { get; set; }
        public float? upfrontCost { get; set; }
        public int? upfrontCostTermId { get; set; }
        public bool? apiEnabled { get; set; }
        public bool? scrapeEnabled { get; set; }
        public string? scrapeConfig { get; set; }
        public string? requiredQueryVars { get; set; }
        public string? slug { get; set; }
        public bool approved { get; set; }
        public string? imageUrl { get; set; }
        public string? description { get; set; }
    }

    public class AdminStoreEditResponseDTO
    {
        public AdminStoreDTO? store { get; set; }
    }

    public class AdminUpsertStoreRequestDTO
    {
        public string? name { get; set; }
        public string? url { get; set; }
        public string? affiliateCode { get; set; }
        public string? affiliateCodeVar { get; set; }
        public int? brandId { get; set; }
        public float? upfrontCost { get; set; }
        public int? upfrontCostTermId { get; set; }
        public bool? apiEnabled { get; set; }
        public bool? scrapeEnabled { get; set; }
        public string? scrapeConfig { get; set; }
        public string? requiredQueryVars { get; set; }
        public string? slug { get; set; }
        public bool? approved { get; set; }
        public string? description { get; set; }
    }

    public class AdminCreateStoreResponseDTO
    {
        public int id { get; set; }
        public string? name { get; set; }
        public string? url { get; set; }
        public string? slug { get; set; }
        public bool approved { get; set; }
        public string? imageUrl { get; set; }
        public string? description { get; set; }
    }
}
