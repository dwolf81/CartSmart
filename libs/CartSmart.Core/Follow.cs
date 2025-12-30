using Supabase.Postgrest.Models;
using Supabase.Postgrest.Attributes;

namespace CartSmart.API.Models;

[Table("follows")]
public class Follow : BaseModel
{
    [Column("follower_id")]
    public int FollowerId { get; set; }

    [Column("following_id")]
    public int FollowingId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
} 