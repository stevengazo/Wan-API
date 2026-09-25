using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Mapache.Sip.Media;

/// <summary>
/// Deepgram Voice Agent: audio µ-law 8 kHz en binario en ambos sentidos, reconocimiento y síntesis de Deepgram,
/// y como LLM el Custom LLM de Mapache (mismo contexto, conocimiento y herramientas que con ElevenLabs).
/// </summary>
public sealed class DeepgramAgentSession : WebSocketVoiceSession
{
    private DeepgramAgentSession(ILogger logger)
        : base(logger)
    {
    }

    protected override string ProviderName => "Deepgram";

    public static async Task<DeepgramAgentSession> StartAsync(DeepgramAgentOptions options, VoiceCallContext call, ILogger logger, CancellationToken ct)
    {
        var session = new DeepgramAgentSession(logger);
        session.Socket.Options.SetRequestHeader("Authorization", $"Token {options.ApiKey}");
        await session.Socket.ConnectAsync(new Uri(options.BaseUrl), ct);

        var agent = new JsonObject
        {
            ["language"] = options.Language,
            ["listen"] = new JsonObject
            {
                ["provider"] = new JsonObject { ["type"] = "deepgram", ["model"] = options.ListenModel },
            },
            ["think"] = new JsonObject
            {
                ["provider"] = new JsonObject { ["type"] = "open_ai", ["model"] = "mapache" },
                // La identidad de la llamada va en headers: el Custom LLM no recibe el extra body de ElevenLabs.
                ["endpoint"] = new JsonObject
                {
                    ["url"] = options.LlmUrl,
                    ["headers"] = new JsonObject
                    {
                        ["authorization"] = $"Bearer {options.LlmToken}",
                        ["x-mapache-extension"] = call.ExtensionId.ToString(),
                        ["x-mapache-call"] = call.CallId.ToString(),
                        ["x-mapache-campaign"] = call.CampaignId?.ToString() ?? "",
                        ["x-mapache-contact"] = call.ContactId?.ToString() ?? "",
                    },
                },
            },
            ["speak"] = new JsonObject
            {
                ["provider"] = new JsonObject { ["type"] = "deepgram", ["model"] = options.SpeakModel },
            },
        };
        var greeting = call.IsCampaign ? call.OpeningLine : options.Greeting;
        if (!string.IsNullOrWhiteSpace(greeting))
        {
            agent["greeting"] = greeting.Trim();
        }

        await session.SendJsonAsync(new JsonObject
        {
            ["type"] = "Settings",
            ["audio"] = new JsonObject
            {
                ["input"] = new JsonObject { ["encoding"] = "mulaw", ["sample_rate"] = 8000 },
                ["output"] = new JsonObject { ["encoding"] = "mulaw", ["sample_rate"] = 8000, ["container"] = "none" },
            },
            ["agent"] = agent,
        }, ct);

        while (true)
        {
            var message = await session.ReceiveJsonAsync(ct)
                ?? throw new InvalidOperationException("Deepgram cerró el socket al configurar el agente");
            var type = message["type"]?.GetValue<string>();
            if (type == "Welcome")
            {
                session.ConversationId = message["request_id"]?.GetValue<string>();
            }
            else if (type == "SettingsApplied")
            {
                break;
            }
            else if (type == "Error")
            {
                throw new InvalidOperationException($"Deepgram: {Describe(message)}");
            }
        }

        session.StartReceiving();
        return session;
    }

    public override Task SendUserAudioAsync(byte[] audio, CancellationToken ct) =>
        SendAsync(audio, WebSocketMessageType.Binary, ct);

    public override Task SendContextualUpdateAsync(string text, CancellationToken ct) =>
        SendJsonAsync(new JsonObject { ["type"] = "InjectUserMessage", ["content"] = text }, ct);

    protected override void HandleBinary(byte[] data) => OnAudio(data);

    protected override Task HandleTextAsync(JsonObject message)
    {
        switch (message["type"]?.GetValue<string>())
        {
            case "UserStartedSpeaking":
                OnInterrupted();
                break;

            case "ConversationText":
                var content = message["content"]?.GetValue<string>() ?? "";
                if (message["role"]?.GetValue<string>() == "user")
                {
                    OnUserTranscript(content);
                }
                else
                {
                    OnAgentResponse(content);
                }

                break;

            case "Error":
                Logger.LogWarning("Deepgram: {Error}", Describe(message));
                break;

            case "Warning":
                Logger.LogInformation("Deepgram: {Warning}", Describe(message));
                break;
        }

        return Task.CompletedTask;
    }

    private static string Describe(JsonObject message) =>
        message["description"]?.GetValue<string>() ?? message["message"]?.GetValue<string>() ?? message.ToJsonString();
}
