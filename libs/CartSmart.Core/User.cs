using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System.Text.Json.Serialization;

namespace CartSmart.API.Models
{
    [Table("user")]
    public partial class User : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("email_address")]
        public string Email { get; set; } = string.Empty;

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

        [Column("bio")]
        public string Bio { get; set; }

        [Column("image_url")]        
        public string? ImageUrl { get; set; }

        [Column("email_opt_in")]
        public bool EmailOptIn { get; set; }

        [Column("active")]
        public bool Active { get; set; }

        [Column("deleted")]
        public bool Deleted { get; set; }

        [Column("deals_posted")]
        public int DealsPosted { get; set; }

        [Column("allow_review")]
        public bool AllowReview { get; set; }

        [Column("admin")]
        public bool Admin { get; set; }

        [Column("email_confirmed")]
        public bool EmailConfirmed { get; set; }

        [Column("password_hash")]
        public string? PasswordHash { get; set; }          // null for pure SSO until user sets one

        [Column("sso_provider")]
        public string? SsoProvider { get; set; }           // e.g. "Google","Apple"

        [Column("sso_subject")]
        public string? SsoSubject { get; set; }            // provider unique sub/subject

        [Column("password_last_changed_utc")]
        public DateTime? PasswordLastChangedUtc { get; set; }

        [Column("failed_login_count")]
        public int FailedLoginCount { get; set; }

        [Column("lockout_until_utc")]
        public DateTime? LockoutUntilUtc { get; set; }

        [Column("canonical_email")]
        public string? CanonicalEmail { get; set; }

        [Column("token_version")]
        public int TokenVersion { get; set; } = 1;

        [Column("terms_version")]
        public string? TermsVersion { get; set; }

        [Column("terms_accepted_at_utc")]
        public DateTime? TermsAcceptedAtUtc { get; set; }
    }
}