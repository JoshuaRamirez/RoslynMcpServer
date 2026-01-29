namespace TestProject.Models;

/// <summary>
/// Represents a user in the system.
/// This type should be moved to its own file.
/// </summary>
public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public Address? Address { get; set; }
}

/// <summary>
/// Represents an address.
/// This type should be moved to its own file.
/// </summary>
public class Address
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}

/// <summary>
/// Represents an order.
/// This type can be moved to a different namespace.
/// </summary>
public class Order
{
    public int Id { get; set; }
    public User? Customer { get; set; }
    public List<OrderItem> Items { get; set; } = new();
    public decimal Total => Items.Sum(i => i.Price * i.Quantity);
}

/// <summary>
/// Represents an order line item.
/// </summary>
public class OrderItem
{
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}
