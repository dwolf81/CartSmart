namespace CartSmart.API.Models.DTOs;

public class ProductPriceHistoryDTO
{
    public List<ProductPriceHistorySeriesDTO> Series { get; set; } = new();
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}

public class ProductPriceHistorySeriesDTO
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public decimal? CurrentPrice { get; set; }
    public decimal? LowestPrice { get; set; }
    public List<ProductPriceHistoryPointDTO> Points { get; set; } = new();
}

public class ProductPriceHistoryPointDTO
{
    public DateTime Date { get; set; }
    public decimal Price { get; set; }
}