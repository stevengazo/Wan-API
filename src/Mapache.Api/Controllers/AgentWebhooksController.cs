using System.Text.Json;
using Mapache.Api.Realtime;
using Mapache.Api.Webhooks;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Recordings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

/// <summary>Entradas desde ElevenLabs que no son tools de un dominio concreto.</summary>
[ApiController]
public sealed class AgentWebhooksController(
    MapacheDbContext db,
    RecordingService recordings,
    TimeProvider time,
    IHubContext<EventsHub, IEventsClient> events,
    ILogger<AgentWebhooksController> logger) : ControllerBase
{
    [HttpPost("api/agent-tools/ping")]
    [Authorize(Policy = AgentToolAuthenticationHandler.PolicyName)]
    public IActionResult Ping() => Ok(new { ok = true, receivedAt = time.GetUtcNow() });

    /// <summary>
    /// Webhook post-llamada. <c>post_call_audio</c> trae el MP3 completo en base64 y se guarda si la grabación
    /// está activada; la transcripción (<c>post_call_transcription</c>) se persiste en Fase 2.
    /// </summary>
    [HttpPost("api/webhooks/elevenlabs/post-call")]
    [AllowAnonymous]
    [RequireElevenLabsSignature]
    // El audio de una llamada larga en base64 supera el límite por defecto de 30 MB.
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> PostCall(JsonElement payload, CancellationToken ct)
    {
        var type = payload.TryGetProperty("type", out var t) ? t.GetString() : null;
        var data = payload.TryGetProperty("data", out var d) ? d : default;
        var conversationId = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("conversation_id", out var id)
            ? id.GetString()
            : null;
        logger.LogInformation("Webhook post-llamada {Type} de la conversación {ConversationId}", type, conversationId);

        if (type == "post_call_audio" && conversationId is not null && data.TryGetProperty("full_audio", out var audio))
        {
            await SaveRecordingAsync(conversationId, audio.GetString(), ct);
        }

        return Ok();
    }

    private async Task SaveRecordingAsync(string conversationId, string? base64, CancellationToken ct)
    {
        var settings = await db.CallSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        if (settings is not { RecordCalls: true } || string.IsNullOrEmpty(base64))
        {
            return;
        }

        // ElevenLabs reintenta los webhooks: una conversación se guarda una sola vez.
        if (await db.Recordings.AnyAsync(r => r.ConversationId == conversationId, ct))
        {
            return;
        }

        var bytes = Convert.FromBase64String(base64);
        using var content = new MemoryStream(bytes);
        await recordings.SaveAsync(conversationId, content, "audio/mpeg", null, null, ct);
        await events.Clients.All.RecordingsChanged();
        logger.LogInformation("Grabación guardada de {ConversationId} ({Bytes} bytes)", conversationId, bytes.LongLength);
    }
}
