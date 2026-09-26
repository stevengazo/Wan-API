using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Wan.Sip.Media;

/// <summary>
/// OpenAI Realtime por WebSocket: audio µ-law 8 kHz en ambos sentidos (sin convertir), detección de turnos del
/// servidor y herramientas de Wan ejecutadas aquí cuando el modelo las pide.
/// </summary>
public sealed class OpenAiRealtimeSession : WebSocketVoiceSession
{
    private readonly BotBrainSession _brain;

    private OpenAiRealtimeSession(BotBrainSession brain, ILogger logger)
        : base(logger) => _brain = brain;

    protected override string ProviderName => "OpenAI Realtime";

    public static async Task<OpenAiRealtimeSession> StartAsync(
        OpenAiRealtimeOptions options, BotBrainSession brain, VoiceCallContext call, ILogger logger, CancellationToken ct)
    {
        var session = new OpenAiRealtimeSession(brain, logger);
        session.Socket.Options.SetRequestHeader("Authorization", $"Bearer {options.ApiKey}");
        await session.Socket.ConnectAsync(new Uri($"{options.BaseUrl}?model={Uri.EscapeDataString(options.Model)}"), ct);

        var instructions = brain.Instructions;
        // En campañas abre la frase de la campaña; en entrantes, el saludo de la recepción.
        var greeting = call.IsCampaign ? call.OpeningLine : options.Greeting;
        if (!string.IsNullOrWhiteSpace(greeting))
        {
            instructions += $"\n\nAl empezar la llamada di estas palabras: \"{greeting.Trim()}\"";
        }

        instructions += call.IsCampaign
            ? $"\n\nEsta llamada la haces tú: llamas al número {call.CallerNumber} desde {call.ExtensionName}."
            : $"\n\nNúmero de quien llama: {call.CallerNumber}. Extensión que atiende: {call.ExtensionName}.";

        var input = new JsonObject
        {
            ["format"] = new JsonObject { ["type"] = "audio/pcmu" },
            ["turn_detection"] = new JsonObject { ["type"] = "semantic_vad" },
        };
        if (!string.IsNullOrWhiteSpace(options.TranscriptionModel))
        {
            input["transcription"] = new JsonObject { ["model"] = options.TranscriptionModel };
        }

        await session.SendJsonAsync(new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "realtime",
                ["model"] = options.Model,
                ["output_modalities"] = new JsonArray("audio"),
                ["instructions"] = instructions,
                ["audio"] = new JsonObject
                {
                    ["input"] = input,
                    ["output"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcmu" },
                        ["voice"] = options.Voice,
                    },
                },
                ["tools"] = new JsonArray(brain.Tools.Select(t => (JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone(),
                }).ToArray()),
                ["tool_choice"] = "auto",
            },
        }, ct);

        // Se espera la confirmación: si la configuración es inválida (modelo, formato) conviene saberlo antes
        // de contestar la llamada y no con el bot mudo.
        while (true)
        {
            var message = await session.ReceiveJsonAsync(ct)
                ?? throw new InvalidOperationException("OpenAI cerró el socket al configurar la sesión");
            var type = message["type"]?.GetValue<string>();
            if (type == "session.updated")
            {
                break;
            }

            if (type == "error")
            {
                throw new InvalidOperationException($"OpenAI Realtime: {ErrorText(message)}");
            }
        }

        session.StartReceiving();
        // Que el bot hable primero, como una recepción: sin esto espera a que quien llama diga algo.
        await session.SendJsonAsync(new JsonObject { ["type"] = "response.create" }, ct);
        return session;
    }

    public override Task SendUserAudioAsync(byte[] audio, CancellationToken ct) =>
        SendJsonAsync(new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = Convert.ToBase64String(audio) }, ct);

    public override Task SendContextualUpdateAsync(string text, CancellationToken ct) =>
        SendJsonAsync(new JsonObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JsonObject
            {
                ["type"] = "message",
                ["role"] = "system",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
            },
        }, ct);

    private readonly HashSet<string> _seen = [];

    protected override async Task HandleTextAsync(JsonObject message)
    {
        var eventType = message["type"]?.GetValue<string>() ?? "";
        if (_seen.Add(eventType))
        {
            // Primera vez de cada tipo de evento: ayuda a diagnosticar cambios de la API sin inundar el log.
            Logger.LogDebug("OpenAI Realtime: primer evento {Type}", eventType);
        }

        switch (eventType)
        {
            case "response.output_audio.delta":
                if (message["delta"]?.GetValue<string>() is { Length: > 0 } delta)
                {
                    OnAudio(Convert.FromBase64String(delta));
                }

                break;

            case "input_audio_buffer.speech_started":
                // La detección del servidor ya cancela la respuesta en curso; aquí se descarta lo que falta sonar.
                OnInterrupted();
                break;

            case "conversation.item.input_audio_transcription.completed":
                OnUserTranscript(message["transcript"]?.GetValue<string>() ?? "");
                break;

            case "response.output_audio_transcript.done":
                OnAgentResponse(message["transcript"]?.GetValue<string>() ?? "");
                break;

            case "response.done":
                await RunToolCallsAsync(message["response"]?["output"] as JsonArray);
                break;

            case "error":
                Logger.LogWarning("OpenAI Realtime: {Error}", ErrorText(message));
                break;
        }
    }

    /// <summary>Ejecuta las herramientas que pidió el modelo, devuelve los resultados y le pide seguir.</summary>
    private async Task RunToolCallsAsync(JsonArray? output)
    {
        var calls = output?.OfType<JsonObject>().Where(o => o["type"]?.GetValue<string>() == "function_call").ToList() ?? [];
        if (calls.Count == 0)
        {
            return;
        }

        foreach (var call in calls)
        {
            var name = call["name"]?.GetValue<string>() ?? "";
            string result;
            try
            {
                result = await _brain.ExecuteToolAsync(name, call["arguments"]?.GetValue<string>() ?? "{}", Lifetime);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "La herramienta {Tool} falló", name);
                result = $"Error al ejecutar la herramienta: {ex.Message}";
            }

            Logger.LogInformation("Herramienta {Tool} ejecutada", name);
            await SendJsonAsync(new JsonObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = call["call_id"]?.GetValue<string>(),
                    ["output"] = result,
                },
            }, Lifetime);
        }

        await SendJsonAsync(new JsonObject { ["type"] = "response.create" }, Lifetime);
    }

    private static string ErrorText(JsonObject message) =>
        message["error"]?["message"]?.GetValue<string>() ?? message.ToJsonString();
}
