using Mapache.Api.Audit;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Mapache.Api.Auth;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record UserListItemDto(
    Guid Id,
    string Email,
    string DisplayName,
    UserRole Role,
    DateTimeOffset CreatedAt,
    bool IsCurrent,
    bool MfaEnabled,
    IReadOnlyList<ExternalProvider> ExternalLogins);

public sealed record CreateUserRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string DisplayName,
    [Required(ErrorMessage = Messages.Required), EmailAddress(ErrorMessage = "Correo inválido"), StringLength(256, ErrorMessage = Messages.MaxLength)] string Email,
    [Required(ErrorMessage = Messages.Required), StringLength(128, MinimumLength = 8, ErrorMessage = "Entre {2} y {1} caracteres")] string Password,
    UserRole Role = UserRole.Operator);

public sealed record UpdateUserRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string DisplayName,
    UserRole Role);

public sealed record ResetPasswordRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, MinimumLength = 8, ErrorMessage = "Entre {2} y {1} caracteres")] string Password);

/// <summary>Usuarios del panel y sus roles. Siempre queda al menos un administrador.</summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
public sealed class UsersController(MapacheDbContext db, IPasswordHasher<User> hasher, TimeProvider time, AuditLog audit) : ControllerBase
{
    [HttpGet("api/users")]
    public async Task<IReadOnlyList<UserListItemDto>> List(CancellationToken ct)
    {
        var current = CurrentUserId();
        return (await db.Users.AsNoTracking().Include(u => u.ExternalLogins).OrderBy(u => u.DisplayName).ToListAsync(ct))
            .Select(u => ToDto(u, current))
            .ToList();
    }

    [HttpPost("api/users")]
    public async Task<ActionResult<UserListItemDto>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            ModelState.AddModelError(nameof(CreateUserRequest.Email), "Ya existe una cuenta con ese correo");
            return ValidationProblem(ModelState);
        }

        var user = new User { Email = email, DisplayName = request.DisplayName.Trim(), Role = request.Role, CreatedAt = time.GetUtcNow() };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.UserCreated, user.Email, user.Id.ToString(), $"Rol: {user.Role}");
        return ToDto(user, CurrentUserId());
    }

    [HttpPut("api/users/{id:guid}")]
    public async Task<ActionResult<UserListItemDto>> Update(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.ExternalLogins).SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        if (user.Role == UserRole.Admin && request.Role != UserRole.Admin && await IsLastAdminAsync(id, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Tiene que quedar al menos un administrador");
        }

        var previousRole = user.Role;
        user.DisplayName = request.DisplayName.Trim();
        user.Role = request.Role;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            AuditActions.UserUpdated, user.Email, user.Id.ToString(), previousRole == user.Role ? null : $"Rol: {previousRole} → {user.Role}");
        return ToDto(user, CurrentUserId());
    }

    /// <summary>Contraseña nueva elegida por un administrador (p. ej. si la persona la olvidó).</summary>
    [HttpPost("api/users/{id:guid}/password")]
    public async Task<IActionResult> ResetPassword(Guid id, ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return NotFound();
        }

        user.PasswordHash = hasher.HashPassword(user, request.Password);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.UserPasswordReset, user.Email, user.Id.ToString());
        return NoContent();
    }

    /// <summary>Para quien perdió el teléfono y los códigos de recuperación: vuelve a entrar solo con la contraseña.</summary>
    [HttpDelete("api/users/{id:guid}/mfa")]
    public async Task<IActionResult> ResetMfa(Guid id, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return NotFound();
        }

        MfaService.Disable(user);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.MfaDisabled, user.Email, user.Id.ToString(), "Quitado por un administrador");
        return NoContent();
    }

    [HttpDelete("api/users/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (id == CurrentUserId())
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "No puedes eliminar tu propia cuenta");
        }

        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return NotFound();
        }

        if (user.Role == UserRole.Admin && await IsLastAdminAsync(id, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Tiene que quedar al menos un administrador");
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.UserDeleted, user.Email, user.Id.ToString());
        return NoContent();
    }

    private Task<bool> IsLastAdminAsync(Guid id, CancellationToken ct) =>
        db.Users.AllAsync(u => u.Id == id || u.Role != UserRole.Admin, ct);

    private Guid? CurrentUserId() => Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : null;

    private static UserListItemDto ToDto(User u, Guid? current) => new(
        u.Id, u.Email, u.DisplayName, u.Role, u.CreatedAt, u.Id == current, u.MfaEnabled, u.ExternalLogins.Select(l => l.Provider).Order().ToList());
}
