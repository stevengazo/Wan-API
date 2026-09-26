using System.ComponentModel.DataAnnotations;
using Mapache.Api.Audit;
using Mapache.Api.Auth;
using Mapache.Api.Sip;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

/// <param name="GoogleRedirectUri">La que hay que registrar en Google Cloud; sale de la URL pública.</param>
public sealed record SsoSettingsDto(
    bool GoogleEnabled,
    string? GoogleClientId,
    bool HasGoogleClientSecret,
    bool MicrosoftEnabled,
    string? MicrosoftClientId,
    bool HasMicrosoftClientSecret,
    string MicrosoftTenant,
    bool AllowRegistration,
    string? AllowedDomains,
    string GoogleRedirectUri,
    string MicrosoftRedirectUri);

/// <summary>Secretos vacíos conservan los guardados.</summary>
public sealed record SaveSsoSettingsRequest(
    bool GoogleEnabled,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? GoogleClientId,
    [StringLength(512, ErrorMessage = Messages.MaxLength)] string? GoogleClientSecret,
    bool MicrosoftEnabled,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? MicrosoftClientId,
    [StringLength(512, ErrorMessage = Messages.MaxLength)] string? MicrosoftClientSecret,
    [StringLength(64, ErrorMessage = Messages.MaxLength)] string? MicrosoftTenant,
    bool AllowRegistration,
    [StringLength(512, ErrorMessage = Messages.MaxLength)] string? AllowedDomains);

[ApiController]
[Authorize(Roles = Roles.Admin)]
public sealed class SsoSettingsController(MapacheDbContext db, ISecretProtector protector, PublicUrlResolver publicUrl, TimeProvider time, AuditLog audit)
    : ControllerBase
{
    [HttpGet("api/settings/sso")]
    public async Task<SsoSettingsDto> Get(CancellationToken ct) =>
        await ToDtoAsync(await db.SsoSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new SsoSettings(), ct);

    [HttpPut("api/settings/sso")]
    public async Task<ActionResult<SsoSettingsDto>> Save(SaveSsoSettingsRequest request, CancellationToken ct)
    {
        var settings = await db.SsoSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new SsoSettings();
            db.SsoSettings.Add(settings);
        }

        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        settings.GoogleEnabled = request.GoogleEnabled;
        settings.GoogleClientId = Optional(request.GoogleClientId);
        settings.MicrosoftEnabled = request.MicrosoftEnabled;
        settings.MicrosoftClientId = Optional(request.MicrosoftClientId);
        settings.MicrosoftTenant = Optional(request.MicrosoftTenant)?.ToLowerInvariant() ?? "organizations";
        settings.AllowRegistration = request.AllowRegistration;
        settings.AllowedDomains = Optional(request.AllowedDomains)?.ToLowerInvariant();
        if (Optional(request.GoogleClientSecret) is { } googleSecret)
        {
            settings.GoogleClientSecretEncrypted = protector.Protect(googleSecret);
        }

        if (Optional(request.MicrosoftClientSecret) is { } microsoftSecret)
        {
            settings.MicrosoftClientSecretEncrypted = protector.Protect(microsoftSecret);
        }

        if (settings.GoogleEnabled)
        {
            Require(settings.GoogleClientId, nameof(SaveSsoSettingsRequest.GoogleClientId));
            Require(settings.GoogleClientSecretEncrypted, nameof(SaveSsoSettingsRequest.GoogleClientSecret));
        }

        if (settings.MicrosoftEnabled)
        {
            Require(settings.MicrosoftClientId, nameof(SaveSsoSettingsRequest.MicrosoftClientId));
            Require(settings.MicrosoftClientSecretEncrypted, nameof(SaveSsoSettingsRequest.MicrosoftClientSecret));
            if (settings.MicrosoftTenant is not ("common" or "organizations" or "consumers") && !Guid.TryParse(settings.MicrosoftTenant, out _))
            {
                ModelState.AddModelError(nameof(SaveSsoSettingsRequest.MicrosoftTenant), "Usa organizations, common o el ID del inquilino (GUID)");
            }
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        settings.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            AuditActions.SettingsChanged,
            "Inicio de sesión con Google y Microsoft",
            details: $"Google {(settings.GoogleEnabled ? "activo" : "inactivo")}, Microsoft {(settings.MicrosoftEnabled ? "activo" : "inactivo")}, registro {(settings.AllowRegistration ? "abierto" : "cerrado")}");
        return await ToDtoAsync(settings, ct);
    }

    private void Require(string? value, string field)
    {
        if (value is null)
        {
            ModelState.AddModelError(field, Messages.Required);
        }
    }

    private async Task<SsoSettingsDto> ToDtoAsync(SsoSettings s, CancellationToken ct)
    {
        var panel = (await publicUrl.ResolveAsync(ct))?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
        return new SsoSettingsDto(
            s.GoogleEnabled, s.GoogleClientId, s.GoogleClientSecretEncrypted is not null,
            s.MicrosoftEnabled, s.MicrosoftClientId, s.MicrosoftClientSecretEncrypted is not null, s.MicrosoftTenant,
            s.AllowRegistration, s.AllowedDomains,
            ExternalAuthController.CallbackUrl(panel, ExternalProvider.Google),
            ExternalAuthController.CallbackUrl(panel, ExternalProvider.Microsoft));
    }
}
