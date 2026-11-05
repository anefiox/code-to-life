using SampleShop.Domain;

namespace SampleShop.Services;

/// <summary>Runs every pricing rule over an order, in order.</summary>
public class PricingEngine(IEnumerable<IPricingRule> rules)
{
    readonly List<IPricingRule> _rules = rules.ToList();

    public decimal Total(Order order)
    {
        var total = order.Subtotal;
        foreach (var rule in _rules)
            total = rule.Apply(order, total);
        return Math.Round(total, 2);
    }
}
