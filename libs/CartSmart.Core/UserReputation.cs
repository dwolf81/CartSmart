using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    // Supabase table model
    [Table("user_reputation")]
    public class UserReputation : BaseModel
    {
        // Ensure the PK maps to user_id
        [PrimaryKey("user_id", false)]
        [Column("user_id")]
        public int UserId { get; set; }

        [Column("submit_total")]
        public int SubmitTotal { get; set; }

        [Column("submit_correct")]
        public int SubmitCorrect { get; set; }

        [Column("flag_total")]
        public int FlagTotal { get; set; }

        [Column("flag_true")]
        public int FlagTrue { get; set; }

        [Column("consecutive_false_flags")]
        public int ConsecutiveFalseFlags { get; set; }

        [Column("penalty_points")]
        public int PenaltyPoints { get; set; }

        [Column("updated_at")]
        public DateTime UpdatedAtUtc { get; set; }
    }

}

