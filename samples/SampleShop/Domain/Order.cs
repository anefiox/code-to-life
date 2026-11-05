namespace SampleShop.Domain;

/// <summary>A customer's order: lines, a status, and the money it adds up to.</summary>
public class Order
{
    readonly List<OrderLine> _lines = [];

    public Order(int id, Customer customer)
    {
        Id = id;
        Customer = customer;
    }

    public int Id { get; }
    public Customer Customer { get; }
    public OrderStatus Status { get; private set; } = OrderStatus.New;
    public IReadOnlyList<OrderLine> Lines => _lines;

    public decimal Subtotal => _lines.Sum(l => l.Total);

    public void Add(Product product, int quantity)
    {
        if (Status != OrderStatus.New) throw new InvalidOperationException($"Order {Id} is already {Status}.");
        var existing = _lines.FindIndex(l => l.Product.Sku == product.Sku);
        if (existing >= 0)
            _lines[existing] = _lines[existing] with { Quantity = _lines[existing].Quantity + quantity };
        else
            _lines.Add(new OrderLine(product, quantity));
    }

    public void MarkPaid() => Status = OrderStatus.Paid;

    public void MarkShipped() =>
        Status = Status == OrderStatus.Paid ? OrderStatus.Shipped : throw new InvalidOperationException("Ship after paying.");
}

public sealed record OrderLine(Product Product, int Quantity)
{
    public decimal Total => Product.Price * Quantity;
}

public enum OrderStatus { New, Paid, Shipped, Cancelled }
