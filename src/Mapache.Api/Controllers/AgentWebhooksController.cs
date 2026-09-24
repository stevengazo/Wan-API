using System.Text.Json;
using Mapache.Api.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mapache.Api.Controllers;

/// <summary>
/// Entradas desde ElevenLabs. Los tools reales (transfer_call, fill_form, ...) llegan en fases siguientes;
/// por ahora solo sirven para verificar que la autenticación está bien configurada del lado del agente.
/// </summary>
[ApiController]
public sealed class AgentWebhooksController(TimeProvider time, ILogger<AgentWebhooksController> logger) : ControllerBase
{
    [HttpPost("api/agent-tools/ping")]
    [Authorize(Policy = AgentToolAuthenticationHandler.PolicyName)]
    public IActionResult Ping() => Ok(new { ok = true, receivedAt = time.GetUtcNow() });

    [HttpPost("api/webhooks/elevenlabs/post-call")]
    [AllowAnonymous]
    [RequireElevenLabsSignature]
    public IActionResult PostCall(JsonElement payload)
    {
        // La persistencia de la llamada y su transcripción es de Fase 2; por ahora solo se registra la recepción.
        var type = payload.TryGetProperty("type", out var t) ? t.GetString() : null;
        var conversationId = payload.TryGetProperty("data", out var data) && data.TryGetProperty("conversation_id", out var id)
            ? id.GetString()
            : null;
        logger.LogInformation("Webhook post-llamada {Type} de la conversación {ConversationId}", type, conversationId);
        return Ok();
    }
}
