using System.Text.Json.Nodes;
using Mapache.Api.Webhooks;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Llm;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mapache.Api.Controllers;

public sealed record LlmProviderDto(LlmProvider Provider, bool Configured, string DefaultModel, bool IsDefault);

[ApiController]
[Route("api/llm")]
public sealed class LlmController(
    MapacheDbContext db,
    ChatCompletionRouter router,
    IOptionsMonitor<LlmOptions> options,
    ILogger<LlmController> logger) : ControllerBase
{
    /// <summary>
    /// Custom LLM del agente de ElevenLabs (su URL se configura como <c>https://&lt;host&gt;/api/llm/v1</c>).
    /// La cuenta SIP llega en <c>elevenlabs_extra_body.extension_id</c>; sin ella se usa el proveedor por defecto.
    /// </summary>
    [HttpPost("v1/chat/completions")]
    [Authorize(Policy = AgentToolAuthenticationHandler.PolicyName)]
    public async Task ChatCompletions([FromBody] JsonObject request, CancellationToken ct)
    {
        var extension = await FindExtensionAsync(request, ct);
        var selection = router.Resolve(extension);

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        // nginx no debe acumular la respuesta: cada fragmento es voz que el llamante está esperando.
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            await router.StreamAsync(selection, request, Response.Body, ct);
        }
        catch (LlmException ex) when (!Response.HasStarted)
        {
            logger.LogWarning(ex, "Custom LLM sin respuesta de {Provider}", selection.Provider);
            Response.ContentType = "application/problem+json";
            Response.StatusCode = StatusCodes.Status502BadGateway;
            await Response.WriteAsJsonAsync(new ProblemDetails { Status = 502, Title = ex.Message }, ct);
        }
        catch (LlmException ex)
        {
            // Ya se envió parte de la respuesta: solo queda cortar el stream.
            logger.LogWarning(ex, "Custom LLM cortado a mitad de respuesta ({Provider})", selection.Provider);
        }
    }

    /// <summary>Proveedores disponibles para el panel: cuáles tienen API key y su modelo por defecto.</summary>
    [HttpGet("providers")]
    [Authorize]
    public IReadOnlyList<LlmProviderDto> Providers()
    {
        var current = options.CurrentValue;
        return Enum.GetValues<LlmProvider>()
            .Select(p => new LlmProviderDto(p, current.For(p).IsConfigured, current.For(p).DefaultModel, p == current.DefaultProvider))
            .ToList();
    }

    private async Task<Extension?> FindExtensionAsync(JsonObject request, CancellationToken ct)
    {
        var raw = request["elevenlabs_extra_body"]?["extension_id"]?.ToString();
        if (!Guid.TryParse(raw, out var id))
        {
            return null;
        }

        return await db.Extensions.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
    }
}
