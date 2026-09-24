namespace Mapache.Domain.Entities;

public enum UserRole
{
    Admin,
    Operator,
}

public class User
{
    public Guid Id { get; set; }

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    public string PasswordHash { get; set; } = "";

    public UserRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
