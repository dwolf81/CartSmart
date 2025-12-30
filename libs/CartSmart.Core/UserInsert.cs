using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System.Text.Json.Serialization;

namespace CartSmart.API.Models
{
    [Table("user")]
    public class UserInsert : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("email_address")]
        public string Email { get; set; } = string.Empty;

        [Column("canonical_email")]
        public string CanonicalEmail { get; set; } = string.Empty;

        [Column("user_name")]
        public string Username { get; set; } = string.Empty;

        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [Column("first_name")]
        public string FirstName { get; set; } = string.Empty;

        [Column("last_name")]
        public string LastName { get; set; } = string.Empty;

        [Column("password")]
        public string Password { get; set; } = string.Empty;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("level")]
        public short Level { get; set; }

        [Column("salt")]
        public string Salt { get; set; }

        [Column("email_opt_in")]
        public bool EmailOptIn { get; set; }

        [Column("active")]
        public bool Active { get; set; }

        [Column("email_confirmed")]
        public bool EmailConfirmed { get; set; }

        [Column("sso_provider")]
        public string? SsoProvider { get; set; }  

    }
}