namespace SampleShop.Domain;

/// <summary>Something the shop sells.</summary>
public sealed record Product(string Sku, string Name, decimal Price, Category Category);

public enum Category { Books, Games, Hardware }
