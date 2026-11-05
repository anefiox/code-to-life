using SampleShop.Domain;

namespace SampleShop.Services;

/// <summary>One way the price of an order can change.</summary>
public interface IPricingRule
{
    string Name { get; }
    decimal Apply(Order order, decimal runningTotal);
}

public sealed class MemberDiscount : IPricingRule
{
    public string Name => "Member discount";
    public decimal Apply(Order order, decimal runningTotal) => runningTotal * (1 - order.Customer.DiscountRate);
}

public sealed class BulkDiscount(int threshold, decimal rate) : IPricingRule
{
    public string Name => $"Bulk {threshold}+";

    public decimal Apply(Order order, decimal runningTotal)
    {
        var items = order.Lines.Sum(l => l.Quantity);
        return items >= threshold ? runningTotal * (1 - rate) : runningTotal;
    }
}

public sealed class Shipping : IPricingRule
{
    public string Name => "Shipping";

    public decimal Apply(Order order, decimal runningTotal) => runningTotal switch
    {
        >= 100m => runningTotal,
        _ when order.Lines.All(l => l.Product.Category == Category.Books) => runningTotal + 2.50m,
        _ => runningTotal + 5m,
    };
}
