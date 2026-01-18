namespace CartSmart.API.Models.DTOs
{
    public class ProductTypeCardDTO
    {
        public int id { get; set; }
        public string? name { get; set; }
        public string? slug { get; set; }
        public List<string> imageUrls { get; set; } = new();
    }
}
