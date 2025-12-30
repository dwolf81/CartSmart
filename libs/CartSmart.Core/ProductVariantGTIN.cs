using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

    [Table("product_variant_gtin")]
    public class ProductVariantGTIN:BaseModel
    {
        [PrimaryKey("id")]
        [JsonIgnore]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("gtin")]
        public string GTIN { get; set; }

        [Column("product_variant_id")]
        public int ProductVariantId { get; set; }




    }