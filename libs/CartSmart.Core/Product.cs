using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

    [Table("product")]
    public class Product:BaseModel
    {
        [PrimaryKey("id")]
        [JsonIgnore]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("name")]
        public string? Name { get; set; }

        [Column("description")]
        public string? Description { get; set; }

        [Column("slug")]
        public string? Slug { get; set; }

        [Column("msrp")]
        public float? MSRP { get; set; }

        [Column("low_price")]
        public float? LowPrice { get; set; }     

        [Column("brand_id")]
        public int? BrandId { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Column("deal_id")]
        public int? DealId { get; set; }

        [Column("image_url")]
        public string? ImageUrl { get; set; }        
        
        [Column("rating")]
        public int? Rating { get; set; }  

        [Column("deleted")]
        public bool Deleted { get; set; }

        // Controls whether background service tasks (refresh/ingest) run for this product.
        // DB column: product.enable_service
        [Column("enable_service")]
        public bool EnableService { get; set; } = true;

        // Optional override for the minimum price sent to the eBay Browse API search filter.
        // When null, the default (30% of MSRP) is used.
        [Column("api_min_price")]
        public float? ApiMinPrice { get; set; }

        // Preferred condition category to accept for this product (1=New, 2=Used, 3=Refurbished)
        [Column("preferred_condition_category_id")]
        public int? PreferredConditionCategoryId { get; set; }

        [Column("product_type_id")]
        public int ProductTypeId { get; set; }        

        // When true, deals for this product track an item count (e.g. golf balls per pack)
        // and the UI shows price-per-item alongside total price.
        [Column("count_enabled")]
        public bool CountEnabled { get; set; }

        // Default item count for new deals on this product (e.g. 12 for a dozen golf balls).
        // Only meaningful when CountEnabled is true.
        [Column("default_count")]
        public int DefaultCount { get; set; } = 1;

        // Navigation property
        public User? User { get; set; }

        public Deal? Deal { get; set; }

        public Brand? Brand { get; set; }
    }