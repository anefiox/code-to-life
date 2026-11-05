namespace SampleShop.Domain;

public class Customer(string name, string email)
{
    public string Name { get; } = name;
    public string Email { get; } = email;
    public virtual decimal DiscountRate => 0m;
}

/// <summary>Members get a discount on everything.</summary>
public sealed class Member(string name, string email, int years) : Customer(name, email)
{
    public int Years { get; } = years;
    public override decimal DiscountRate => Math.Min(0.05m * Years, 0.25m);
}
