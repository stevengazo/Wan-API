using System.Security.Claims;
using Mapache.Api.Audit;
using Mapache.Api.Auth;
using Mapache.Api.Sip;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record ExternalProviderDto(ExternalProvider Provider, string DisplayName);

public sealed record StartExternalLoginRequest(ExternalFlowMode Mode = ExternalFlowMode.Login);

public sealed record StartExternalLoginResponse(string Url);

public sealed record ExternalLoginDto(ExternalProvider Provider, string? Email, DateTimeOffset CreatedAt);

/// <summary>
/// Entrar, registrarse y vincular la cuenta con Google o Microsoft. La vuelta del proveedor termina en el panel:
/// <c>/login/sso#token=…</c> si salió bien, o <c>/login?ssoError=…</c> (<c>/perfil?ssoError=…</c> al vincular).
/// </summary>
[ApiController]
[Route("api/auth/external")]
public sealed class ExternalAuthController(
    MapacheDbContext db,
    ExternalAuthService external,
    TokenService tokens,
    PublicUrlResolver publicUrl,
    TimeProvider time,
    AuditLog audit) : ControllerBase
{
    [HttpGet("providers")]
    [AllowAnonymous]
    public async Task<IReadOnlyList<ExternalProviderDto>> Providers(CancellationToken ct)
    {
        var settings = await db.SsoSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new SsoSettings();
        return Enum.GetValues<ExternalProvider>()
            .Where(p => ExternalAuthService.IsConfigured(settings, p))
            .Select(p => new ExternalProviderDto(p, ExternalAuthService.DisplayName(p)))
            .ToList();
    }

    /// <summary>
    /// Devuelve la URL del proveedor en vez de redirigir: así la llamada lleva el JWT al vincular. Deja una cookie
    /// que ata la vuelta a este navegador.
    /// </summary>
    [HttpPost("{provider}/start")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<StartExternalLoginResponse>> Start(ExternalProvider provider, StartExternalLoginRequest request, CancellationToken ct)
    {
        var settings = await db.SsoSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new SsoSettings();
        if (!ExternalAuthService.IsConfigured(settings, provider))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: $"El inicio de sesión con {ExternalAuthService.DisplayName(provider)} no está habilitado");
        }

        Guid? userId = null;
        if (request.Mode == ExternalFlowMode.Link)
        {
            if (!Guid.TryParse(User.FindFirstValue("sub"), out var id))
            {
                return Unauthorized();
            }

            userId = id;
        }

        var flow = ExternalAuthService.NewFlow(provider, request.Mode, userId);
        Response.Cookies.Append(ExternalAuthService.BindingCookie, flow.Binding, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            // Lax: la vuelta del proveedor es una navegación de primer nivel y la cookie tiene que viajar.
            SameSite = SameSiteMode.Lax,
            Path = "/api/auth/external",
            MaxAge = ExternalAuthService.FlowLifetime,
        });
        var url = await external.AuthorizeUrlAsync(settings, flow, await CallbackUrlAsync(provider, ct), ct);
        return new StartExternalLoginResponse(url);
    }

    [HttpGet("{provider}/callback")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Callback(
        ExternalProvider provider, [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        var panel = await PanelUrlAsync(ct);
        var flow = external.ReadState(state);
        var binding = Request.Cookies[ExternalAuthService.BindingCookie];
        Response.Cookies.Delete(ExternalAuthService.BindingCookie, new CookieOptions { Path = "/api/auth/external" });

        var errorPage = flow?.Mode == ExternalFlowMode.Link ? "/perfil" : "/login";
        IActionResult Fail(string message) => Redirect(QueryHelpers.AddQueryString($"{panel}{errorPage}", "ssoError", message));

        if (flow is null || flow.Provider != provider || binding is null || binding != flow.Binding)
        {
            return Fail("El inicio de sesión venció o se abrió en otro navegador; vuelve a intentarlo");
        }

        if (error is not null || code is null)
        {
            // access_denied: la persona canceló en la pantalla del proveedor.
            return Fail(error == "access_denied" ? "Se canceló el inicio de sesión" : $"{ExternalAuthService.DisplayName(provider)} devolvió un error: {error}");
        }

        var settings = await db.SsoSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new SsoSettings();
        if (!ExternalAuthService.IsConfigured(settings, provider))
        {
            return Fail($"El inicio de sesión con {ExternalAuthService.DisplayName(provider)} no está habilitado");
        }

        ExternalIdentity identity;
        try
        {
            identity = await external.RedeemAsync(settings, flow, code, await CallbackUrlAsync(provider, ct), ct);
        }
        catch (ExternalAuthException ex)
        {
            return Fail(ex.Message);
        }

        var link = await db.ExternalLogins.SingleOrDefaultAsync(l => l.Provider == provider && l.Subject == identity.Subject, ct);
        return flow.Mode == ExternalFlowMode.Link
            ? await LinkAsync(flow, identity, link, panel, Fail, ct)
            : await LoginAsync(settings, identity, link, panel, Fail, ct);
    }

    [HttpGet]
    [Authorize]
    public async Task<IReadOnlyList<ExternalLoginDto>> Linked(CancellationToken ct)
    {
        var id = CurrentUserId();
        return await db.ExternalLogins.AsNoTracking()
            .Where(l => l.UserId == id)
            .OrderBy(l => l.Provider)
            .Select(l => new ExternalLoginDto(l.Provider, l.Email, l.CreatedAt))
            .ToListAsync(ct);
    }

    [HttpDelete("{provider}")]
    [Authorize]
    public async Task<IActionResult> Unlink(ExternalProvider provider, CancellationToken ct)
    {
        var id = CurrentUserId();
        var user = await db.Users.Include(u => u.ExternalLogins).SingleOrDefaultAsync(u => u.Id == id, ct);
        var link = user?.ExternalLogins.SingleOrDefault(l => l.Provider == provider);
        if (user is null || link is null)
        {
            return NotFound();
        }

        if (user.PasswordHash.Length == 0 && user.ExternalLogins.Count == 1)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Es tu única forma de entrar: define una contraseña o vincula otra cuenta antes de desvincularla");
        }

        db.ExternalLogins.Remove(link);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.ExternalUnlinked, user.Email, user.Id.ToString(), ExternalAuthService.DisplayName(provider));
        return NoContent();
    }

    private async Task<IActionResult> LinkAsync(
        ExternalFlow flow, ExternalIdentity identity, ExternalLogin? existing, string panel, Func<string, IActionResult> fail, CancellationToken ct)
    {
        var name = ExternalAuthService.DisplayName(identity.Provider);
        var user = await db.Users.Include(u => u.ExternalLogins).SingleOrDefaultAsync(u => u.Id == flow.UserId, ct);
        if (user is null)
        {
            return fail("Tu sesión ya no es válida; vuelve a entrar");
        }

        if (existing is not null)
        {
            return existing.UserId == user.Id ? Redirect($"{panel}/perfil?sso=linked") : fail($"Esa cuenta de {name} ya está vinculada a otro usuario");
        }

        if (user.ExternalLogins.Any(l => l.Provider == identity.Provider))
        {
            return fail($"Ya tienes una cuenta de {name} vinculada; desvincúlala primero");
        }

        db.ExternalLogins.Add(new ExternalLogin
        {
            UserId = user.Id,
            Provider = identity.Provider,
            Subject = identity.Subject,
            Email = identity.Email,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.ExternalLinked, user.Email, user.Id.ToString(), $"{name}: {identity.Email}", user.Email, user.Id);
        return Redirect($"{panel}/perfil?sso=linked");
    }

    private async Task<IActionResult> LoginAsync(
        SsoSettings settings, ExternalIdentity identity, ExternalLogin? link, string panel, Func<string, IActionResult> fail, CancellationToken ct)
    {
        var name = ExternalAuthService.DisplayName(identity.Provider);
        User? user;
        if (link is not null)
        {
            user = await db.Users.SingleAsync(u => u.Id == link.UserId, ct);
        }
        else
        {
            if (!settings.AllowRegistration)
            {
                return fail($"Esa cuenta de {name} no está vinculada a ningún usuario. Entra con tu contraseña y vincúlala desde tu perfil");
            }

            if (identity.Email is null || (identity.Provider == ExternalProvider.Google && !identity.EmailVerified))
            {
                return fail($"{name} no compartió un correo verificado");
            }

            if (!DomainAllowed(settings, identity.Email))
            {
                await audit.RecordAsync(AuditActions.LoginFailed, identity.Email, details: $"{name}: dominio no permitido", userEmail: identity.Email);
                return fail("Tu correo no pertenece a un dominio permitido");
            }

            // Nunca se vincula por coincidencia de correo: quien controle ese correo en otro proveedor se quedaría con
            // la cuenta (y saltaría su doble factor).
            if (await db.Users.AnyAsync(u => u.Email == identity.Email, ct))
            {
                return fail($"Ya existe una cuenta con {identity.Email}. Entra con tu contraseña y vincula {name} desde tu perfil");
            }

            user = new User
            {
                Email = identity.Email,
                DisplayName = string.IsNullOrWhiteSpace(identity.Name) ? identity.Email : identity.Name.Trim(),
                Role = UserRole.Operator,
                CreatedAt = time.GetUtcNow(),
            };
            user.ExternalLogins.Add(new ExternalLogin
            {
                Provider = identity.Provider,
                Subject = identity.Subject,
                Email = identity.Email,
                CreatedAt = time.GetUtcNow(),
            });
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(AuditActions.UserCreated, user.Email, user.Id.ToString(), $"Registro con {name}; rol: {user.Role}", user.Email, user.Id);
        }

        // El doble factor lo exige el proveedor (la política de la empresa en Google Workspace o Entra ID).
        var (token, expiresAt) = tokens.Create(user);
        await audit.RecordAsync(AuditActions.LoginSucceeded, user.Email, user.Id.ToString(), name, user.Email, user.Id);
        var fragment = QueryHelpers.AddQueryString("", new Dictionary<string, string?> { ["token"] = token, ["expiresAt"] = expiresAt.ToString("O") });
        // En el fragmento (#) el token no llega a ningún servidor ni queda en logs de proxies.
        return Redirect($"{panel}/login/sso#{fragment.TrimStart('?')}");
    }

    public static bool DomainAllowed(SsoSettings settings, string email)
    {
        var domains = (settings.AllowedDomains ?? "")
            .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.TrimStart('@').ToLowerInvariant())
            .ToList();
        var domain = email[(email.LastIndexOf('@') + 1)..].ToLowerInvariant();
        return domains.Count == 0 || domains.Contains(domain);
    }

    private async Task<string> PanelUrlAsync(CancellationToken ct) =>
        (await publicUrl.ResolveAsync(ct))?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";

    private async Task<string> CallbackUrlAsync(ExternalProvider provider, CancellationToken ct) =>
        CallbackUrl(await PanelUrlAsync(ct), provider);

    internal static string CallbackUrl(string panel, ExternalProvider provider) =>
        $"{panel}/api/auth/external/{provider.ToString().ToLowerInvariant()}/callback";

    private Guid? CurrentUserId() => Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : null;
}
