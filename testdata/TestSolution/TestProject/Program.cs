using TestProject.Models;
using TestProject.Services;

var userService = new UserService();
var orderService = new OrderService();

// Create a user
var user = userService.CreateUser("John Doe", "john@example.com");
userService.SetAddress(user, new Address
{
    Street = "123 Main St",
    City = "Seattle",
    Country = "USA"
});

// Create an order
var order = orderService.CreateOrder(user);
orderService.AddItem(order, "Widget", 9.99m, 2);
orderService.AddItem(order, "Gadget", 19.99m, 1);

Console.WriteLine($"User: {user.Name}");
Console.WriteLine($"Address: {user.Address?.Street}, {user.Address?.City}");
Console.WriteLine($"Order Total: ${order.Total}");
