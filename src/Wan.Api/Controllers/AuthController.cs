using Wan.Api.Audit;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Wan.Api.Auth;
using Wan.Domain.Entities;
using Wan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Wan.Api.Controllers;

public sealed record LoginRequest(
    [Required(ErrorMessage = Messages.Required), EmailAddress(ErrorMessage = "Correo inválido")] string Email,
    [Required(ErrorMessage = Messages.Required)] string Password);

public sealed record RegisterRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string DisplayName,
    [Required(ErrorMessage = Messages.Required), EmailAddress(ErrorMessage = "Correo inválido"), StringLength(256, ErrorMessage = Messages.MaxLength)] string Email,
    [Required(ErrorMessage = Messages.Required), StringLength(128, MinimumLength = 8, ErrorMessage = "Entre {2} y {1} caracteres")] string Password);

public sealed record UpdateProfileRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string DisplayName);

/// <summary>La contraseña actual no se pide a quien entró siempre con Google o Microsoft y todavía no tiene una.</summary>
public sealed record ChangePasswordRequest(
    string? CurrentPassword,
    [Required(ErrorMessage = Messages.Required), StringLength(128, MinimumLength = 8, ErrorMessage = "Entre {2} y {1} caracteres")] string NewPassword);

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role, bool MfaEnabled, bool HasPassword);

/// <summary>
/// El JWT va en la cookie HttpOnly de sesión, nunca en el cuerpo. Con doble factor, el primer paso solo devuelve
/// <see cref="MfaToken"/> para canjearlo en <c>login/mfa</c>.
/// </summary>
public sealed record LoginResponse(DateTimeOffset? ExpiresAt, UserDto? User, bool MfaRequired = false, string? MfaToken = null);

public sealed record MfaLoginRequest(
    [Required(ErrorMessage = Messages.Required)] string MfaToken,
    [Required(ErrorMessage = Messages.Required)] string Code);

public sealed record MfaCodeRequest([Required(ErrorMessage = Messages.Required)] string Code);

public sealed record MfaStatusDto(bool Enabled, int RecoveryCodesLeft);

public sealed record MfaSetupDto(string Secret, string OtpAuthUri);

public sealed record MfaRecoveryCodesDto(IReadOnlyList<string> RecoveryCodes);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(WanDbContext db, IPasswordHasher<User> hasher, TokenService tokens, MfaService mfa, TimeProvider time, AuditLog audit)
    : ControllerBase
{
    /// <summary>
    /// Registro de arranque: solo funciona mientras no exista ningún usuario y crea el primer
    /// administrador. Una vez que hay cuentas, queda cerrado y los demás usuarios los crea un
    /// administrador desde <c>/api/users</c>; así nadie puede escalar a Admin desde internet.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        // Cerrado en cuanto existe al menos una cuenta: el registro abierto solo sirve para el primer admin.
        if (await db.Users.AnyAsync(ct))
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "El registro está cerrado; pide a un administrador que te cree la cuenta");
        }

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
        SessionCookie.Append(HttpContext, token, expiresAt);
        return new LoginResponse(expiresAt, ToDto(user));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);

        // Mismo error para usuario inexistente y contraseña incorrecta: no revelar qué correos existen.
        if (user is null || !PasswordMatches(user, request.Password))
        {
            await audit.RecordAsync(AuditActions.LoginFailed, email, user?.Id.ToString(), userEmail: email, userId: user?.Id);
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Correo o contraseña incorrectos");
        }

        if (user.MfaEnabled)
        {
            return new LoginResponse(null, null, MfaRequired: true, MfaToken: mfa.CreateChallenge(user));
        }

        return await CompleteLoginAsync(user, "Contraseña");
    }

    /// <summary>Segundo paso: código de la app autenticadora o uno de recuperación.</summary>
    [HttpPost("login/mfa")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> LoginMfa(MfaLoginRequest request, CancellationToken ct)
    {
        var challenge = mfa.ReadChallenge(request.MfaToken);
        var user = challenge is { } c ? await db.Users.SingleOrDefaultAsync(u => u.Id == c.UserId, ct) : null;
        if (user is null || !MfaService.StillValid(user, challenge!.Value.Fingerprint))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El inicio de sesión venció; vuelve a ingresar la contraseña");
        }

        var check = mfa.Verify(user, request.Code);
        if (check == MfaCheck.Invalid)
        {
            await audit.RecordAsync(AuditActions.LoginFailed, user.Email, user.Id.ToString(), "Código de doble factor incorrecto", user.Email, user.Id);
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Código incorrecto");
        }

        await db.SaveChangesAsync(ct);
        return await CompleteLoginAsync(user, check == MfaCheck.RecoveryCode ? "Contraseña y código de recuperación" : "Contraseña y doble factor");
    }

    /// <summary>Borra la cookie de sesión. El JWT sigue siendo válido hasta vencer si alguien lo copió antes.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public IActionResult Logout()
    {
        SessionCookie.Delete(HttpContext);
        return NoContent();
    }

    /// <summary>Sesión actual (usuario y vencimiento del JWT de la cookie), p. ej. al volver de Google o Microsoft.</summary>
    [HttpGet("session")]
    [Authorize]
    public async Task<ActionResult<LoginResponse>> Session(CancellationToken ct)
    {
        if (await CurrentUserAsync(ct) is not { } user || !long.TryParse(User.FindFirstValue("exp"), out var exp))
        {
            return Unauthorized();
        }

        return new LoginResponse(DateTimeOffset.FromUnixTimeSeconds(exp), ToDto(user));
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

        if (user.PasswordHash.Length > 0 && !PasswordMatches(user, request.CurrentPassword ?? ""))
        {
            ModelState.AddModelError(nameof(ChangePasswordRequest.CurrentPassword), "La contraseña actual no es correcta");
            return ValidationProblem(ModelState);
        }

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.PasswordChanged, user.Email, user.Id.ToString());
        return NoContent();
    }

    [HttpGet("mfa")]
    [Authorize]
    public async Task<ActionResult<MfaStatusDto>> MfaStatus(CancellationToken ct) =>
        await CurrentUserAsync(ct) is { } user ? new MfaStatusDto(user.MfaEnabled, user.MfaRecoveryCodeHashes.Count) : Unauthorized();

    /// <summary>Semilla nueva para escanear; no se activa hasta confirmar un código.</summary>
    [HttpPost("mfa/setup")]
    [Authorize]
    public async Task<ActionResult<MfaSetupDto>> MfaSetup(CancellationToken ct)
    {
        if (await CurrentUserAsync(ct) is not { } user)
        {
            return Unauthorized();
        }

        if (user.MfaEnabled)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "El doble factor ya está activo; desactívalo para configurar otra app");
        }

        var (secret, uri) = mfa.BeginSetup(user);
        await db.SaveChangesAsync(ct);
        return new MfaSetupDto(secret, uri);
    }

    [HttpPost("mfa/enable")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<MfaRecoveryCodesDto>> MfaEnable(MfaCodeRequest request, CancellationToken ct)
    {
        if (await CurrentUserAsync(ct) is not { } user)
        {
            return Unauthorized();
        }

        if (user.MfaEnabled || user.MfaSecretEncrypted is null)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Primero genera el código QR");
        }

        if (mfa.Verify(user, request.Code, allowRecovery: false) == MfaCheck.Invalid)
        {
            ModelState.AddModelError(nameof(MfaCodeRequest.Code), "Código incorrecto; revisa que la hora del teléfono esté bien");
            return ValidationProblem(ModelState);
        }

        user.MfaEnabled = true;
        var codes = mfa.NewRecoveryCodes(user);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.MfaEnabled, user.Email, user.Id.ToString());
        return new MfaRecoveryCodesDto(codes);
    }

    [HttpPost("mfa/disable")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> MfaDisable(MfaCodeRequest request, CancellationToken ct)
    {
        if (await CurrentUserAsync(ct) is not { } user)
        {
            return Unauthorized();
        }

        if (!user.MfaEnabled || mfa.Verify(user, request.Code) == MfaCheck.Invalid)
        {
            ModelState.AddModelError(nameof(MfaCodeRequest.Code), "Código incorrecto");
            return ValidationProblem(ModelState);
        }

        MfaService.Disable(user);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.MfaDisabled, user.Email, user.Id.ToString());
        return NoContent();
    }

    [HttpPost("mfa/recovery-codes")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<MfaRecoveryCodesDto>> MfaRecoveryCodes(MfaCodeRequest request, CancellationToken ct)
    {
        if (await CurrentUserAsync(ct) is not { } user)
        {
            return Unauthorized();
        }

        if (!user.MfaEnabled || mfa.Verify(user, request.Code, allowRecovery: false) == MfaCheck.Invalid)
        {
            ModelState.AddModelError(nameof(MfaCodeRequest.Code), "Código incorrecto");
            return ValidationProblem(ModelState);
        }

        var codes = mfa.NewRecoveryCodes(user);
        await db.SaveChangesAsync(ct);
        return new MfaRecoveryCodesDto(codes);
    }

    private async Task<ActionResult<LoginResponse>> CompleteLoginAsync(User user, string method)
    {
        var (token, expiresAt) = tokens.Create(user);
        SessionCookie.Append(HttpContext, token, expiresAt);
        await audit.RecordAsync(AuditActions.LoginSucceeded, user.Email, user.Id.ToString(), method, user.Email, user.Id);
        return new LoginResponse(expiresAt, ToDto(user));
    }

    // Las cuentas creadas con Google o Microsoft no tienen contraseña: nunca entran por aquí.
    private bool PasswordMatches(User user, string password) =>
        user.PasswordHash.Length > 0 && hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;

    private async Task<User?> CurrentUserAsync(CancellationToken ct) =>
        Guid.TryParse(User.FindFirstValue("sub"), out var id)
            ? await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct)
            : null;

    internal static UserDto ToDto(User user) => new(user.Id, user.Email, user.DisplayName, user.Role, user.MfaEnabled, user.PasswordHash.Length > 0);
}
