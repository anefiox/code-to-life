using SampleShop.Domain;

namespace SampleShop.Infrastructure;

public interface IRepository<T>
{
    void Save(T item);
    T? Find(int id);
    IEnumerable<T> All();
}

/// <summary>Keeps orders in memory, keyed by id.</summary>
public sealed class InMemoryOrderRepository : IRepository<Order>
{
    readonly Dictionary<int, Order> _orders = [];

    public void Save(Order item) => _orders[item.Id] = item;
    public Order? Find(int id) => _orders.GetValueOrDefault(id);
    public IEnumerable<Order> All() => _orders.Values;
}
