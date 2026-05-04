using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("social_post_caption")]
public class SocialPostCaption : BaseModel
{
    [PrimaryKey("id")]
    [JsonIgnore]
    public long Id { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("social_post_id")]
    public long SocialPostId { get; set; }

    [Column("caption_text")]
    public string? CaptionText { get; set; }

    /// <summary>
    /// Platform target: all | twitter | facebook | instagram
    /// </summary>
    [Column("platform")]
    public string Platform { get; set; } = "all";

    [Column("selected")]
    public bool Selected { get; set; }
}
