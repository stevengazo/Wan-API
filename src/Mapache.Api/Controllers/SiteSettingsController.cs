using System.ComponentModel.DataAnnotations;
using Mapache.Api.Auth;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mapache.Api.Controllers;

/// <summary>Opciones del sitio que se leen del entorno (sección App).</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>URL pública por defecto; la del panel tiene prioridad.</summary>
    public string? PublicUrl { get; set; }
}

/// <param name="PublicUrl">La guardada en el panel.</param>
/// <param name="EnvironmentUrl">La de <c>App:PublicUrl</c>, para mostrarla como valor por defecto.</param>
/// <param name="EffectiveUrl">La que se usa: la del panel, si no la del entorno.</param>
public sealed record SiteSettingsDto(string? PublicUrl, string? EnvironmentUrl, string? EffectiveUrl);

public sealed record SaveSiteSettingsRequest(
    [MaxLength(512, ErrorMessage = Messages.MaxLength)] string? PublicUrl);

[ApiController]
public sealed class SiteSettingsController(MapacheDbContext db, IOptions<AppOptions> options, TimeProvider time) : ControllerBase
{
    [HttpGet("api/settings/site")]
    [Authorize]
    public async Task<SiteSettingsDto> Get(CancellationToken ct)
    {
        var settings = await db.SiteSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        return ToDto(settings?.PublicUrl);
    }

    [HttpPut("api/settings/site")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<SiteSettingsDto>> Save(SaveSiteSettingsRequest request, CancellationToken ct)
    {
        string? url = null;
        if (!string.IsNullOrWhiteSpace(request.PublicUrl))
        {
            if (Normalize(request.PublicUrl) is not { } normalized)
            {
                ModelState.AddModelError(nameof(request.PublicUrl), "Usa una URL completa, por ejemplo https://mapache.miempresa.com");
                return ValidationProblem(ModelState);
            }

            url = normalized;
        }

        var settings = await db.SiteSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new SiteSettings();
            db.SiteSettings.Add(settings);
        }

        settings.PublicUrl = url;
        settings.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDto(settings.PublicUrl);
    }

    private SiteSettingsDto ToDto(string? publicUrl)
    {
        var environment = Normalize(options.Value.PublicUrl);
        return new SiteSettingsDto(publicUrl, environment, publicUrl ?? environment);
    }

    /// <summary>Solo http(s) absoluto, sin la barra final para poder concatenar rutas.</summary>
    private static string? Normalize(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;
}
