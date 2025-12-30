namespace CartSmart.API.Models;

public class Vote
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public int DealId { get; set; }
    public Deal Deal { get; set; } = null!;

    public bool IsUpvote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
} 