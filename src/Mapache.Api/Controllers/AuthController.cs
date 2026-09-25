using Mapache.Api.Audit;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Mapache.Api.Auth;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record LoginRequest(
    [Required(ErrorMessage = Messages.Required), EmailAddress(ErrorMessage = "Correo inválido")] string Email,
    [Required(ErrorMessage = Messages.Required)] string Password);

public sealed record RegisterRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string DisplayName,
    [Required(ErrorMessage = Messages.Required), EmailAddress(ErrorMessage = "Correo inválido"), StringLength(256, ErrorMessage = Messages.MaxLength)] string Email,
    [Required(ErrorMessage = Messages.Required), StringLength(128, MinimumLength = 8, ErrorMessage = "Entre {2} y {1} caracteres")] string Password);

public sealed record UpdateProfileRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string DisplayName);

public sealed record ChangePasswordRequest(
    [Required(ErrorMessage = Messages.Required)] string CurrentPassword,
    [Required(ErrorMessage = Messages.Required), StringLength(128, MinimumLength = 8, ErrorMessage = "Entre {2} y {1} caracteres")] string NewPassword);

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, UserDto User);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(MapacheDbContext db, IPasswordHasher<User> hasher, TokenService tokens, TimeProvider time, AuditLog audit) : ControllerBase
{
    /// <summary>
    /// Registro abierto: la cuenta queda activa al instante con rol <see cref="UserRole.Admin"/>.
    /// Cualquiera con acceso a la URL obtiene control total; cerrar o restringir antes de exponer el panel.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            ModelState.AddModelError(nameof(RegisterRequest.Email), "Ya existe una cuenta con ese correo");
            return ValidationProblem(ModelState);
        }

        var user = new User
        {
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            Role = UserRole.Admin,
            CreatedAt = time.GetUtcNow(),
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        var (token, expiresAt) = tokens.Create(user);
        return new LoginResponse(token, expiresAt, ToDto(user));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);

        // Mismo error para usuario inexistente y contraseña incorrecta: no revelar qué correos existen.
        if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            await audit.RecordAsync(AuditActions.LoginFailed, email, user?.Id.ToString(), userEmail: email, userId: user?.Id);
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Correo o contraseña incorrectos");
        }

        var (token, expiresAt) = tokens.Create(user);
        await audit.RecordAsync(AuditActions.LoginSucceeded, user.Email, user.Id.ToString(), userEmail: user.Email, userId: user.Id);
        return new LoginResponse(token, expiresAt, ToDto(user));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var id))
        {
            return Unauthorized();
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? Unauthorized() : ToDto(user);
    }

    [HttpPut("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> UpdateProfile(UpdateProfileRequest request, CancellationToken ct)
    {
        if (await CurrentUserAsync(ct) is not { } user)
        {
            return Unauthorized();
        }

        user.DisplayName = request.DisplayName.Trim();
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    [HttpPost("password")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        if (await CurrentUserAsync(ct) is not { } user)
        {
            return Unauthorized();
        }

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            ModelState.AddModelError(nameof(ChangePasswordRequest.CurrentPassword), "La contraseña actual no es correcta");
            return ValidationProblem(ModelState);
        }

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.PasswordChanged, user.Email, user.Id.ToString());
        return NoContent();
    }

    private async Task<User?> CurrentUserAsync(CancellationToken ct) =>
        Guid.TryParse(User.FindFirstValue("sub"), out var id)
            ? await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct)
            : null;

    private static UserDto ToDto(User user) => new(user.Id, user.Email, user.DisplayName, user.Role);
}
