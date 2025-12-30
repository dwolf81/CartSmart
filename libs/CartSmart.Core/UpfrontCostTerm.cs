using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;


namespace CartSmart.API.Models
{

    [Table("upfront_cost_term")]
    public class UpfrontCostTerm : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("name")]
        public string Name { get; set; } = default!;   // Display label (One Time, Monthly, Annually)
    }
}