namespace SampleShop.Infrastructure;

/// <summary>How many of each product are on the shelf.</summary>
public class Inventory
{
    readonly Dictionary<string, int> _stock = new(StringComparer.Ordinal);
    readonly Lock _lock = new();

    public void Restock(string sku, int quantity)
    {
        lock (_lock) _stock[sku] = _stock.GetValueOrDefault(sku) + quantity;
    }

    public bool TryReserve(string sku, int quantity)
    {
        lock (_lock)
        {
            var available = _stock.GetValueOrDefault(sku);
            if (available < quantity) return false;
            _stock[sku] = available - quantity;
            return true;
        }
    }

    public void Release(string sku, int quantity) => Restock(sku, quantity);

    public int InStock(string sku)
    {
        lock (_lock) return _stock.GetValueOrDefault(sku);
    }
}
