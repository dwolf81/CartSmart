using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace CartSmart.API.Models.DTOs
{
    public class BrandDTO
    {
        [JsonProperty("id")]
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonProperty("url")]
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    public class AdminCreateBrandRequestDTO
    {
        [JsonProperty("name")]
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonProperty("url")]
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
