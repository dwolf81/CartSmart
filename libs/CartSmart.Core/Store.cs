using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;


namespace CartSmart.API.Models
{

    [Table("store")]
    public class Store:BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("name")]
        public string? Name { get; set; }

        [Column("url")]
        public string? URL { get; set; }

        [Column("affiliate_code")]
        public string? AffiliateCode { get; set; }

        [Column("affiliate_code_var")]
        public string? AffiliateCodeVar { get; set; }
        
        [Column("brand_id")]
        public int? BrandId { get; set; }

        [Column("upfront_cost")]
        public float? UpfrontCost { get; set; }

        [Column("upfront_cost_term_id")]
        public int? UpfrontCostTermId { get; set; } 
        
        [Column("api_enabled")]
        public bool? ApiEnabled { get; set; }

        // Enable/disable HTML scraping fallback per store
        [Column("scrape_enabled")]
        public bool? ScrapeEnabled { get; set; }

        // JSONB configuration for scraping selectors (e.g. {"price_selectors":["#price",".offer-price"]})
        [Column("scrape_config")]
        public string? ScrapeConfig { get; set; }

        [Column("required_query_vars")]
        public string? RequiredQueryVars { get; set; } // comma-delimited list; if null/empty keep all params
        // ...existing columns... // FK -> upfront_cost_term.id


    }
} 