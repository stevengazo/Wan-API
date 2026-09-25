using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Mapache.Sip.Media;

/// <summary>
/// Cliente del WebSocket de ElevenLabs Conversational AI (una conversación por llamada). El agente usa el
/// Custom LLM de Mapache, que recibe la extensión y la llamada por <c>custom_llm_extra_body</c>.
/// </summary>
public sealed class ElevenLabsConversation : WebSocketVoiceSession
{
    private ElevenLabsConversation(ILogger logger)
        : base(logger)
    {
    }

    protected override string ProviderName => "ElevenLabs";

    public static async Task<ElevenLabsConversation> StartAsync(
        HttpClient http,
        ElevenLabsOptions options,
        IReadOnlyDictionary<string, string> dynamicVariables,
        ILogger logger,
        CancellationToken ct,
        JsonObject? customLlmExtraBody = null,
        string? firstMessage = null)
    {
        var conversation = new ElevenLabsConversation(logger);
        var url = await GetSignedUrlAsync(http, options, ct);
        await conversation.Socket.ConnectAsync(new Uri(url), ct);

        var initiation = new JsonObject
        {
            ["type"] = "conversation_initiation_client_data",
            ["dynamic_variables"] = JsonSerializer.SerializeToNode(dynamicVariables),
            // Llega al Custom LLM de Mapache como elevenlabs_extra_body (p. ej. qué extensión atiende).
            ["custom_llm_extra_body"] = customLlmExtraBody,
        };
        if (!string.IsNullOrWhiteSpace(firstMessage))
        {
            // Campañas: la primera frase es la de la campaña. El agente debe permitir sobrescribir el primer
            // mensaje (Security → Overrides en ElevenLabs); si no, ElevenLabs rechaza la conversación.
            initiation["conversation_config_override"] = new JsonObject
            {
                ["agent"] = new JsonObject { ["first_message"] = firstMessage },
            };
        }

        await conversation.SendJsonAsync(initiation, ct);

        // El primer mensaje útil es la metadata con los formatos de audio negociados; sin ellos no se puede
        // convertir el audio, así que se espera antes de empezar a puentear.
        while (true)
        {
            var message = await conversation.ReceiveJsonAsync(ct)
                ?? throw new InvalidOperationException("ElevenLabs cerró el socket antes de enviar la metadata");

            if (message["type"]?.GetValue<string>() != "conversation_initiation_metadata")
            {
                continue;
            }

            var metadata = message["conversation_initiation_metadata_event"]!;
            conversation.ConversationId = metadata["conversation_id"]?.GetValue<string>();
            conversation.InputFormat = VoiceAudioFormat.Parse(metadata["user_input_audio_format"]!.GetValue<string>());
            conversation.OutputFormat = VoiceAudioFormat.Parse(metadata["agent_output_audio_format"]!.GetValue<string>());
            break;
        }

        conversation.StartReceiving();
        return conversation;
    }

    public override Task SendUserAudioAsync(byte[] audio, CancellationToken ct) =>
        SendJsonAsync(new JsonObject { ["user_audio_chunk"] = Convert.ToBase64String(audio) }, ct);

    public override Task SendContextualUpdateAsync(string text, CancellationToken ct) =>
        SendJsonAsync(new JsonObject { ["type"] = "contextual_update", ["text"] = text }, ct);

    private static async Task<string> GetSignedUrlAsync(HttpClient http, ElevenLabsOptions options, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{options.BaseUrl.TrimEnd('/')}/v1/convai/conversation/get-signed-url?agent_id={Uri.EscapeDataString(options.AgentId)}");
        request.Headers.Add("xi-api-key", options.ApiKey);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return body?["signed_url"]?.GetValue<string>()
            ?? throw new InvalidOperationException("La respuesta de get-signed-url no trae signed_url");
    }

    protected override async Task HandleTextAsync(JsonObject message)
    {
        switch (message["type"]?.GetValue<string>())
        {
            case "audio":
                var base64 = message["audio_event"]?["audio_base_64"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(base64))
                {
                    OnAudio(Convert.FromBase64String(base64));
                }

                break;

            case "interruption":
                OnInterrupted();
                break;

            case "user_transcript":
                OnUserTranscript(message["user_transcription_event"]?["user_transcript"]?.GetValue<string>() ?? "");
                break;

            case "agent_response":
                OnAgentResponse(message["agent_response_event"]?["agent_response"]?.GetValue<string>() ?? "");
                break;

            case "ping":
                // Sin pong ElevenLabs corta la conversación.
                var eventId = message["ping_event"]?["event_id"]?.GetValue<int>() ?? 0;
                await SendJsonAsync(new JsonObject { ["type"] = "pong", ["event_id"] = eventId }, Lifetime);
                break;
        }
    }
}
