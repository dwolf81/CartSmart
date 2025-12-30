using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("deal")]
    public class DealNav : Deal
    {

          // Navigation property
        [JsonPropertyName("user")]
        public User? User { get; set; }

        [JsonPropertyName("condition")]
        public Condition? Condition { get; set; }

        [JsonPropertyName("deal_type")]
        public DealType? DealType { get; set; }

        [JsonPropertyName("product")]
        public Product? Product { get; set; }

    }
} 