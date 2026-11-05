using SampleShop.Domain;
using SampleShop.Infrastructure;

namespace SampleShop.Services;

public sealed class OrderPlacedEventArgs(Order order, decimal total) : EventArgs
{
    public Order Order { get; } = order;
    public decimal Total { get; } = total;
}

/// <summary>Takes orders from creation to shipping.</summary>
public class OrderService(IRepository<Order> orders, Inventory inventory, PricingEngine pricing, IPaymentGateway payments)
{
    int _nextId = 1;

    public event EventHandler<OrderPlacedEventArgs>? OrderPlaced;

    public Order Start(Customer customer)
    {
        var order = new Order(_nextId++, customer);
        orders.Save(order);
        return order;
    }

    public async Task<bool> CheckoutAsync(Order order)
    {
        foreach (var line in order.Lines)
            if (!inventory.TryReserve(line.Product.Sku, line.Quantity))
                return false;

        var total = pricing.Total(order);
        var paid = await payments.ChargeAsync(order.Customer, total);
        if (!paid)
        {
            foreach (var line in order.Lines) inventory.Release(line.Product.Sku, line.Quantity);
            return false;
        }

        order.MarkPaid();
        orders.Save(order);
        OrderPlaced?.Invoke(this, new OrderPlacedEventArgs(order, total));
        return true;
    }

    public void Ship(Order order)
    {
        order.MarkShipped();
        orders.Save(order);
    }
}
