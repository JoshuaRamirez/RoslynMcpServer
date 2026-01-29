using TestProject.Models;

namespace TestProject.Services;

/// <summary>
/// Service for managing orders.
/// References Order, OrderItem, and User types.
/// </summary>
public class OrderService
{
    private readonly List<Order> _orders = new();

    public Order CreateOrder(User customer)
    {
        var order = new Order
        {
            Id = _orders.Count + 1,
            Customer = customer
        };
        _orders.Add(order);
        return order;
    }

    public void AddItem(Order order, string productName, decimal price, int quantity)
    {
        order.Items.Add(new OrderItem
        {
            ProductName = productName,
            Price = price,
            Quantity = quantity
        });
    }

    public IReadOnlyList<Order> GetOrdersForUser(User user)
    {
        return _orders.Where(o => o.Customer?.Id == user.Id).ToList().AsReadOnly();
    }
}
