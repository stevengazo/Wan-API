using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Mapache.Sip.Poc;

/// <summary>
/// Cliente del WebSocket de ElevenLabs Conversational AI (una conversación por llamada).
/// </summary>
public sealed class ElevenLabsConversation : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private int _disposed;

    private ElevenLabsConversation(ILogger logger) => _logger = logger;

    public string? ConversationId { get; private set; }

    public ElevenLabsAudioFormat InputFormat { get; private set; }

    public ElevenLabsAudioFormat OutputFormat { get; private set; }

    /// <summary>Audio del agente ya decodificado de base64, en <see cref="OutputFormat"/>.</summary>
    public event Action<byte[]>? AudioReceived;

    public event Action? Interrupted;

    public event Action<string>? UserTranscript;

    public event Action<string>? AgentResponse;

    /// <summary>Se dispara cuando el socket se cierra, sea por fin de conversación o por error.</summary>
    public event Action<string>? Closed;

    public static async Task<ElevenLabsConversation> StartAsync(
        HttpClient http,
        ElevenLabsOptions options,
        IReadOnlyDictionary<string, string> dynamicVariables,
        ILogger logger,
        CancellationToken ct)
    {
        var conversation = new ElevenLabsConversation(logger);
        var url = await GetSignedUrlAsync(http, options, ct);
        await conversation._socket.ConnectAsync(new Uri(url), ct);

        await conversation.SendAsync(new JsonObject
        {
            ["type"] = "conversation_initiation_client_data",
            ["dynamic_variables"] = JsonSerializer.SerializeToNode(dynamicVariables),
        }, ct);

        // El primer mensaje útil es la metadata con los formatos de audio negociados; sin ellos no se puede
        // convertir el audio, así que se espera antes de empezar a puentear.
        while (true)
        {
            var message = await conversation.ReceiveMessageAsync(ct)
                ?? throw new InvalidOperationException("ElevenLabs cerró el socket antes de enviar la metadata");

            if (message["type"]?.GetValue<string>() != "conversation_initiation_metadata")
            {
                continue;
            }

            var metadata = message["conversation_initiation_metadata_event"]!;
            conversation.ConversationId = metadata["conversation_id"]?.GetValue<string>();
            conversation.InputFormat = ElevenLabsAudioFormat.Parse(metadata["user_input_audio_format"]!.GetValue<string>());
            conversation.OutputFormat = ElevenLabsAudioFormat.Parse(metadata["agent_output_audio_format"]!.GetValue<string>());
            break;
        }

        conversation._receiveLoop = Task.Run(conversation.ReceiveLoopAsync);
        return conversation;
    }

    public Task SendUserAudioAsync(byte[] audio, CancellationToken ct) =>
        SendAsync(new JsonObject { ["user_audio_chunk"] = Convert.ToBase64String(audio) }, ct);

    public Task SendContextualUpdateAsync(string text, CancellationToken ct) =>
        SendAsync(new JsonObject { ["type"] = "contextual_update", ["text"] = text }, ct);

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

    private async Task ReceiveLoopAsync()
    {
        var reason = "fin de conversación";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(_cts.Token);
                if (message is null)
                {
                    reason = $"ElevenLabs cerró el socket ({_socket.CloseStatus} {_socket.CloseStatusDescription})";
                    break;
                }

                await HandleMessageAsync(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            reason = $"error en el socket: {ex.Message}";
            _logger.LogError(ex, "Error recibiendo de ElevenLabs");
        }

        Closed?.Invoke(reason);
    }

    private async Task HandleMessageAsync(JsonObject message)
    {
        switch (message["type"]?.GetValue<string>())
        {
            case "audio":
                var base64 = message["audio_event"]?["audio_base_64"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(base64))
                {
                    AudioReceived?.Invoke(Convert.FromBase64String(base64));
                }

                break;

            case "interruption":
                Interrupted?.Invoke();
                break;

            case "user_transcript":
                UserTranscript?.Invoke(message["user_transcription_event"]?["user_transcript"]?.GetValue<string>() ?? "");
                break;

            case "agent_response":
                AgentResponse?.Invoke(message["agent_response_event"]?["agent_response"]?.GetValue<string>() ?? "");
                break;

            case "ping":
                // Sin pong ElevenLabs corta la conversación.
                var eventId = message["ping_event"]?["event_id"]?.GetValue<int>() ?? 0;
                await SendAsync(new JsonObject { ["type"] = "pong", ["event_id"] = eventId }, _cts.Token);
                break;
        }
    }

    private async Task<JsonObject?> ReceiveMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return JsonNode.Parse(stream.ToArray()) as JsonObject;
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // CloseOutputAsync y no CloseAsync: el loop de recepción sigue leyendo en paralelo y dos
        // ReceiveAsync concurrentes no están permitidos.
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "llamada terminada", timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
        }

        await _cts.CancelAsync();
        if (_receiveLoop is not null)
        {
            await _receiveLoop;
        }

        _socket.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
