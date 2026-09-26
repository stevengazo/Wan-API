using Wan.Api.Audit;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Wan.Api.Auth;
using Wan.Api.Webhooks;
using Wan.Domain.Entities;
using Wan.Infrastructure.Llm;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Wan.Api.Controllers;

/// <summary>La API key nunca sale de la API; solo se informa si está cargada.</summary>
public sealed record LlmProviderSettingsDto(LlmProvider Provider, string? Model, string DefaultModel, bool HasApiKey);

public sealed record LlmSettingsDto(LlmProvider ActiveProvider, string? SystemPrompt, IReadOnlyList<LlmProviderSettingsDto> Providers);

/// <summary>
/// <c>ApiKey</c> nula o vacía conserva la guardada; <c>ClearApiKey</c> la borra.
/// </summary>
public sealed record SaveLlmProviderRequest(
    LlmProvider Provider,
    [StringLength(128, ErrorMessage = Messages.MaxLength)] string? Model,
    [StringLength(512, ErrorMessage = Messages.MaxLength)] string? ApiKey,
    bool ClearApiKey = false);

public sealed record SaveLlmSettingsRequest(
    LlmProvider ActiveProvider,
    [StringLength(8000, ErrorMessage = Messages.MaxLength)] string? SystemPrompt,
    [Required(ErrorMessage = Messages.Required)] IReadOnlyList<SaveLlmProviderRequest> Providers);

[ApiController]
[Route("api/llm")]
public sealed class LlmController(
    WanDbContext db,
    ChatCompletionRouter router,
    ISecretProtector protector,
    IOptionsMonitor<LlmOptions> options,
    TimeProvider time,
    ILogger<LlmController> logger,
    AuditLog audit) : ControllerBase
{
    /// <summary>
    /// Custom LLM del agente de voz. ElevenLabs lo usa como <c>https://&lt;host&gt;/api/llm/v1</c>; Deepgram arma la
    /// URL desde la base <c>https://&lt;host&gt;/api/llm</c>, así que se aceptan ambas rutas.
    /// </summary>
    [HttpPost("v1/chat/completions")]
    [HttpPost("chat/completions")]
    [Authorize(Policy = AgentToolAuthenticationHandler.PolicyName)]
    public async Task ChatCompletions([FromBody] JsonObject request, CancellationToken ct)
    {
        // Deepgram no manda el extra body de ElevenLabs: la llamada viaja en headers y se normaliza aquí.
        if (request["elevenlabs_extra_body"] is null
            && Request.Headers["x-wan-extension"].ToString() is { Length: > 0 } extensionId)
        {
            request["elevenlabs_extra_body"] = new JsonObject
            {
                ["extension_id"] = extensionId,
                ["call_id"] = Request.Headers["x-wan-call"].ToString(),
                ["campaign_id"] = Request.Headers["x-wan-campaign"].ToString(),
                ["contact_id"] = Request.Headers["x-wan-contact"].ToString(),
            };
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        // nginx no debe acumular la respuesta: cada fragmento es voz que el llamante está esperando.
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            await router.StreamAsync(request, Response.Body, ct);
        }
        catch (LlmException ex) when (!Response.HasStarted)
        {
            logger.LogWarning(ex, "Custom LLM sin respuesta del proveedor");
            Response.ContentType = "application/problem+json";
            Response.StatusCode = StatusCodes.Status502BadGateway;
            await Response.WriteAsJsonAsync(new ProblemDetails { Status = 502, Title = ex.Message }, ct);
        }
        catch (LlmException ex)
        {
            // Ya se envió parte de la respuesta: solo queda cortar el stream.
            logger.LogWarning(ex, "Custom LLM cortado a mitad de respuesta");
        }
    }

    [HttpGet("settings")]
    [Authorize]
    public async Task<LlmSettingsDto> GetSettings(CancellationToken ct)
    {
        var settings = await db.LlmSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new LlmSettings();
        var stored = await db.LlmProviderSettings.AsNoTracking().ToDictionaryAsync(p => p.Provider, ct);
        return ToDto(settings, stored);
    }

    [HttpPut("settings")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<LlmSettingsDto> SaveSettings(SaveLlmSettingsRequest request, CancellationToken ct)
    {
        var settings = await db.LlmSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new LlmSettings();
            db.LlmSettings.Add(settings);
        }

        settings.ActiveProvider = request.ActiveProvider;
        settings.SystemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt.Trim();
        settings.UpdatedAt = time.GetUtcNow();

        var stored = await db.LlmProviderSettings.ToDictionaryAsync(p => p.Provider, ct);
        foreach (var item in request.Providers)
        {
            if (!stored.TryGetValue(item.Provider, out var provider))
            {
                provider = new LlmProviderSettings { Provider = item.Provider };
                db.LlmProviderSettings.Add(provider);
                stored[item.Provider] = provider;
            }

            provider.Model = string.IsNullOrWhiteSpace(item.Model) ? null : item.Model.Trim();
            if (item.ClearApiKey)
            {
                provider.ApiKeyEncrypted = null;
            }
            else if (!string.IsNullOrWhiteSpace(item.ApiKey))
            {
                provider.ApiKeyEncrypted = protector.Protect(item.ApiKey.Trim());
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.SettingsChanged, "Inteligencia artificial", details: $"Proveedor activo: {settings.ActiveProvider}");
        return ToDto(settings, stored);
    }

    private LlmSettingsDto ToDto(LlmSettings settings, IReadOnlyDictionary<LlmProvider, LlmProviderSettings> stored) =>
        new(settings.ActiveProvider, settings.SystemPrompt, Enum.GetValues<LlmProvider>()
            .Select(p => new LlmProviderSettingsDto(
                p,
                stored.GetValueOrDefault(p)?.Model,
                options.CurrentValue.For(p).DefaultModel,
                stored.GetValueOrDefault(p)?.ApiKeyEncrypted is not null))
            .ToList());
}
