using CartSmart.API.Models;

namespace CartSmart.Core.Worker;

public static class EnumsExtensions
{
    // Map Store table name to StoreType heuristic (placeholder - real mapping should read Store data)
    public static StoreType ToStoreType(this Store store)
    {
        var name = store.Name?.ToLowerInvariant() ?? string.Empty;
        if (name.Contains("ebay")) return StoreType.Ebay;
        if (name.Contains("amazon")) return StoreType.Amazon;
        if (name.Contains("best buy") || name.Contains("bestbuy")) return StoreType.BestBuy;
        if (name.Contains("walmart")) return StoreType.Walmart;
        return StoreType.Generic;
    }
}