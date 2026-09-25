using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Mapache.Sip.Media;

/// <summary>Proveedor de voz activo con sus credenciales, tal como lo configura el panel.</summary>
public abstract record VoiceProviderOptions
{
    /// <summary>Nombre para los logs y los motivos de fin de llamada.</summary>
    public abstract string DisplayName { get; }
}

/// <summary>Agente de ElevenLabs con el que se abre cada conversación (su LLM es el Custom LLM de Mapache).</summary>
public sealed record ElevenLabsOptions(string ApiKey, string AgentId, string BaseUrl = "https://api.elevenlabs.io") : VoiceProviderOptions
{
    public override string DisplayName => "ElevenLabs";
}

/// <summary>OpenAI Realtime: voz a voz con el modelo de OpenAI; Mapache le da instrucciones y herramientas.</summary>
public sealed record OpenAiRealtimeOptions(
    string ApiKey,
    string Model,
    string Voice,
    string? TranscriptionModel,
    string? Greeting,
    string BaseUrl = "wss://api.openai.com/v1/realtime") : VoiceProviderOptions
{
    public override string DisplayName => "OpenAI Realtime";
}

/// <summary>
/// Deepgram Voice Agent: reconocimiento y síntesis de Deepgram; las respuestas las da el Custom LLM de Mapache
/// (<paramref name="LlmUrl"/>, autenticado con <paramref name="LlmToken"/>).
/// </summary>
public sealed record DeepgramAgentOptions(
    string ApiKey,
    string Language,
    string ListenModel,
    string SpeakModel,
    string? Greeting,
    string LlmUrl,
    string LlmToken,
    string BaseUrl = "wss://agent.deepgram.com/v1/agent/converse") : VoiceProviderOptions
{
    public override string DisplayName => "Deepgram";
}

/// <summary>Herramienta que el proveedor de voz puede pedir; Mapache la ejecuta.</summary>
public sealed record VoiceToolSpec(string Name, string Description, JsonObject Parameters);

/// <summary>Instrucciones y herramientas del bot para una llamada, más cómo ejecutar las herramientas.</summary>
public sealed record BotBrainSession(
    string Instructions,
    IReadOnlyList<VoiceToolSpec> Tools,
    Func<string, string, CancellationToken, Task<string>> ExecuteToolAsync);

/// <summary>
/// El "cerebro" del bot para proveedores que traen su propio modelo (OpenAI Realtime): lo implementa la API
/// con el mismo contexto y herramientas que el Custom LLM.
/// </summary>
public interface IBotBrain
{
    Task<BotBrainSession> StartAsync(Guid extensionId, Guid callId, CancellationToken ct);
}

/// <summary>Datos de la llamada que necesita el proveedor de voz.</summary>
public sealed record VoiceCallContext(Guid ExtensionId, Guid CallId, string CallerNumber, string ExtensionName);

/// <summary>Abre la conversación con el proveedor configurado.</summary>
public static class VoiceSessionFactory
{
    public static async Task<IVoiceSession> StartAsync(
        VoiceProviderOptions options, VoiceCallContext call, HttpClient http, IBotBrain brain, ILogger logger, CancellationToken ct)
    {
        switch (options)
        {
            case ElevenLabsOptions elevenLabs:
                return await ElevenLabsConversation.StartAsync(
                    http,
                    elevenLabs,
                    new Dictionary<string, string> { ["caller_id"] = call.CallerNumber, ["extension_name"] = call.ExtensionName },
                    logger,
                    ct,
                    new JsonObject { ["extension_id"] = call.ExtensionId.ToString(), ["call_id"] = call.CallId.ToString() });

            case OpenAiRealtimeOptions openAi:
                var session = await brain.StartAsync(call.ExtensionId, call.CallId, ct);
                return await OpenAiRealtimeSession.StartAsync(openAi, session, call, logger, ct);

            case DeepgramAgentOptions deepgram:
                return await DeepgramAgentSession.StartAsync(deepgram, call, logger, ct);

            default:
                throw new NotSupportedException($"Proveedor de voz no soportado: {options.GetType().Name}");
        }
    }
}
