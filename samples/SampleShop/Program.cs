using SampleShop.Domain;
using SampleShop.Infrastructure;
using SampleShop.Services;

// A tiny shop that keeps taking orders, so there's always something to watch.
// Run with --once to place a handful of orders and exit (handy for stepping through).
var once = args.Contains("--once");

var catalog = new[]
{
    new Product("BK-1", "C# in Depth", 39.99m, Category.Books),
    new Product("BK-2", "Roslyn Cookbook", 24.50m, Category.Books),
    new Product("GM-1", "Space Trader", 59.00m, Category.Games),
    new Product("HW-1", "Mechanical Keyboard", 129.00m, Category.Hardware),
};

var inventory = new Inventory();
foreach (var product in catalog) inventory.Restock(product.Sku, 50);

var pricing = new PricingEngine([new MemberDiscount(), new BulkDiscount(threshold: 5, rate: 0.1m), new Shipping()]);
var service = new OrderService(new InMemoryOrderRepository(), inventory, pricing, new FakePaymentGateway(seed: 42));
var ledger = new SalesLedger();
var recommendations = new RecommendationEngine();
service.OrderPlaced += (_, e) => ledger.Record(e.Order, e.Total);

var customers = new Customer[]
{
    new("Ada", "ada@example.com"),
    new Member("Grace", "grace@example.com", years: 3),
    new Member("Linus", "linus@example.com", years: 7),
};

var random = new Random(7);
for (var round = 1; !once || round <= 5; round++)
{
    var customer = customers[random.Next(customers.Length)];
    var order = service.Start(customer);
    for (var i = random.Next(1, 4); i > 0; i--)
        order.Add(catalog[random.Next(catalog.Length)], random.Next(1, 4));

    var suggestions = recommendations.Suggest(order, catalog);
    var ok = await service.CheckoutAsync(order);
    if (ok)
    {
        service.Ship(order);
        recommendations.Learn(order);
    }
    Console.WriteLine($"#{order.Id,-4} {customer.Name,-6} {order.Lines.Count} line(s) {(ok ? "shipped" : "declined"),-9} ledger {ledger.Total,10:C}  try: {string.Join(", ", suggestions.Select(s => s.Name))}");

    foreach (var product in catalog)
        if (inventory.InStock(product.Sku) < 10) inventory.Restock(product.Sku, 40);

    await Task.Delay(300);
}

/// <summary>Adds up what the shop has sold.</summary>
sealed class SalesLedger
{
    readonly List<(int OrderId, decimal Amount)> _entries = [];
    public decimal Total => _entries.Sum(e => e.Amount);
    public void Record(Order order, decimal amount) => _entries.Add((order.Id, amount));
}
