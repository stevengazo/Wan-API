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

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, UserDto User);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(MapacheDbContext db, IPasswordHasher<User> hasher, TokenService tokens) : ControllerBase
{
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
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Correo o contraseña incorrectos");
        }

        var (token, expiresAt) = tokens.Create(user);
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

    private static UserDto ToDto(User user) => new(user.Id, user.Email, user.DisplayName, user.Role);
}
