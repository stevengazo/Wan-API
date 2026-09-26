using Wan.Domain.Entities;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Wan.Infrastructure.Llm;

/// <summary>Proveedor activo listo para llamar: modelo efectivo, API key descifrada y URL base.</summary>
public sealed record LlmTarget(LlmProvider Provider, string Model, string ApiKey, string BaseUrl, string? SystemPrompt);

/// <summary>Lee la configuración general de la IA (panel → Configuración) y la deja lista para usar.</summary>
public sealed class LlmResolver(WanDbContext db, ISecretProtector protector, IOptionsMonitor<LlmOptions> options)
{
    public async Task<LlmTarget> ResolveAsync(CancellationToken ct)
    {
        var settings = await db.LlmSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new LlmSettings();
        var provider = settings.ActiveProvider;
        var stored = await db.LlmProviderSettings.AsNoTracking().SingleOrDefaultAsync(p => p.Provider == provider, ct);
        var defaults = options.CurrentValue.For(provider);

        if (stored?.ApiKeyEncrypted is not { } encryptedKey)
        {
            throw new LlmException($"El proveedor {provider} no tiene API key configurada");
        }

        var model = string.IsNullOrWhiteSpace(stored.Model) ? defaults.DefaultModel : stored.Model;
        return new LlmTarget(provider, model, protector.Unprotect(encryptedKey), defaults.BaseUrl, settings.SystemPrompt);
    }
}
