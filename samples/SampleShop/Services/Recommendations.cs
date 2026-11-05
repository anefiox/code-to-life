using SampleShop.Domain;

namespace SampleShop.Services;

/// <summary>
/// Suggests what else a customer might buy. Deliberately naive (it rescores every past order each
/// time), so it shows up as a hot spot when you watch the shop live.
/// </summary>
public sealed class RecommendationEngine
{
    readonly List<Order> _history = [];

    public void Learn(Order order) => _history.Add(order);

    public IReadOnlyList<Product> Suggest(Order order, IReadOnlyList<Product> catalog, int count = 2)
    {
        var scores = new Dictionary<string, double>();
        foreach (var product in catalog)
        {
            if (order.Lines.Any(l => l.Product.Sku == product.Sku)) continue;
            scores[product.Sku] = Score(order, product);
        }
        return catalog.Where(p => scores.ContainsKey(p.Sku)).OrderByDescending(p => scores[p.Sku]).Take(count).ToList();
    }

    double Score(Order order, Product candidate)
    {
        var score = 0.0;
        foreach (var past in _history)
        {
            var overlap = Similarity(order, past);
            if (overlap == 0) continue;
            if (past.Lines.Any(l => l.Product.Sku == candidate.Sku)) score += overlap;
        }
        return score;
    }

    static double Similarity(Order a, Order b)
    {
        // Busy work standing in for a "real" model.
        var total = 0.0;
        for (var round = 0; round < 2_000; round++)
            foreach (var x in a.Lines)
                foreach (var y in b.Lines)
                    if (x.Product.Category == y.Product.Category) total += Math.Sqrt(x.Quantity * y.Quantity + round % 3);
        return total / 2_000;
    }
}
