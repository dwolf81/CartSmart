namespace CartSmart.API.Models.DTOs
{
    public class NotificationDTO
    {
        public long Id { get; set; }
        public string TypeCode { get; set; } = "";
        public string Message { get; set; } = "";
        public string? LinkUrl { get; set; }
        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}