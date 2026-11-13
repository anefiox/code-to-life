using SampleShop.Domain;
using SampleShop.Services;

namespace SampleShop.Tests;

// A deliberately partial test suite: pricing is covered, checkout, payments and
// recommendations aren't, so the "Test coverage" overlay has something to show.
public class PricingTests
{
    static readonly Product Book = new("B-1", "A book", 20m, Category.Books);
    static readonly Product Console = new("H-1", "A console", 300m, Category.Hardware);

    [Fact]
    public void Members_get_their_discount()
    {
        var order = new Order(1, new Member("Ada", "ada@example.com", years: 2));
        order.Add(Console, 1);
        var engine = new PricingEngine([new MemberDiscount()]);
        Assert.Equal(270m, engine.Total(order));
    }

    [Fact]
    public void Small_book_orders_pay_cheaper_shipping()
    {
        var order = new Order(2, new Customer("Bob", "bob@example.com"));
        order.Add(Book, 1);
        Assert.Equal(22.50m, new PricingEngine([new Shipping()]).Total(order));
    }

    [Theory]
    [InlineData(2, 40)]
    [InlineData(5, 90)]
    public void Bulk_discount_starts_at_the_threshold(int quantity, decimal expected)
    {
        var order = new Order(3, new Customer("Cy", "cy@example.com"));
        order.Add(Book, quantity);
        Assert.Equal(expected, new PricingEngine([new BulkDiscount(threshold: 5, rate: 0.1m)]).Total(order));
    }
}
