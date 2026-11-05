using SampleShop.Domain;

namespace SampleShop.Infrastructure;

public interface IPaymentGateway
{
    Task<bool> ChargeAsync(Customer customer, decimal amount);
}

/// <summary>Pretends to talk to a card processor: slow, and declines now and then.</summary>
public sealed class FakePaymentGateway(int seed) : IPaymentGateway
{
    readonly Random _random = new(seed);

    public async Task<bool> ChargeAsync(Customer customer, decimal amount)
    {
        await Task.Delay(_random.Next(50, 250));
        return amount < 500m && _random.NextDouble() > 0.1;
    }
}
