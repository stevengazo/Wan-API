using Wan.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wan.Infrastructure.Persistence;

public static class DbInitializer
{
    /// <summary>
    /// Aplica migraciones y, si no hay usuarios, crea un admin inicial tomado de <c>Auth:BootstrapAdmin</c>.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WanDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DbInitializer));

        await db.Database.MigrateAsync(ct);

        if (!await db.Users.AnyAsync(ct))
        {
            var email = config["Auth:BootstrapAdmin:Email"];
            var password = config["Auth:BootstrapAdmin:Password"];
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                logger.LogWarning("No hay usuarios y falta Auth:BootstrapAdmin:Email/Password: nadie podrá iniciar sesión");
            }
            else
            {
                var admin = new User
                {
                    Email = email.Trim().ToLowerInvariant(),
                    DisplayName = "Administrador",
                    Role = UserRole.Admin,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                admin.PasswordHash = new PasswordHasher<User>().HashPassword(admin, password);
                db.Users.Add(admin);
                logger.LogInformation("Admin inicial creado: {Email}", admin.Email);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
