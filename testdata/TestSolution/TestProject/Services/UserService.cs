using TestProject.Models;

namespace TestProject.Services;

/// <summary>
/// Service for managing users.
/// References User and Address types from Models namespace.
/// </summary>
public class UserService
{
    private readonly List<User> _users = new();

    public User CreateUser(string name, string email)
    {
        var user = new User
        {
            Id = _users.Count + 1,
            Name = name,
            Email = email
        };
        _users.Add(user);
        return user;
    }

    public void SetAddress(User user, Address address)
    {
        user.Address = address;
    }

    public IReadOnlyList<User> GetAllUsers() => _users.AsReadOnly();
}
