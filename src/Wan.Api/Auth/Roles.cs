using Wan.Domain.Entities;

namespace Wan.Api.Auth;

public static class Roles
{
    public const string Admin = nameof(UserRole.Admin);

    public const string Operator = nameof(UserRole.Operator);
}
