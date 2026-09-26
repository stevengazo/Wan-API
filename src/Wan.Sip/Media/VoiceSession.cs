using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Wan.Sip.Media;

/// <summary>
/// Conversación con un proveedor de voz (ElevenLabs, OpenAI Realtime, Deepgram): recibe el audio de quien
/// llama y devuelve el del agente. <see cref="CallBridge"/> no sabe qué proveedor hay detrás.
/// </summary>
public interface IVoiceSession : IAsyncDisposable
{
    /// <summary>Id de la conversación en el proveedor, si lo tiene (ElevenLabs lo usa para la grabación).</summary>
    string? ConversationId { get; }

    VoiceAudioFormat InputFormat { get; }

    VoiceAudioFormat OutputFormat { get; }

    /// <summary>Audio del agente en <see cref="OutputFormat"/>.</summary>
    event Action<byte[]>? AudioReceived;

    /// <summary>Quien llama empezó a hablar encima del agente: hay que descartar el audio pendiente.</summary>
    event Action? Interrupted;

    event Action<string>? UserTranscript;

    event Action<string>? AgentResponse;

    /// <summary>La conversación terminó (fin normal o error), con el motivo.</summary>
    event Action<string>? Closed;

    /// <summary>Audio de quien llama en <see cref="InputFormat"/>.</summary>
    Task SendUserAudioAsync(byte[] audio, CancellationToken ct);

    /// <summary>Dato que el agente debe tener en cuenta sin que lo haya dicho quien llama (p. ej. una tecla).</summary>
    Task SendContextualUpdateAsync(string text, CancellationToken ct);
}

/// <summary>Base de los proveedores que hablan por WebSocket: envío serializado, loop de recepción y cierre.</summary>
public abstract class WebSocketVoiceSession : IVoiceSession
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private int _disposed;

    protected WebSocketVoiceSession(ILogger logger) => Logger = logger;

    protected ClientWebSocket Socket { get; } = new();

    protected ILogger Logger { get; }

    /// <summary>Nombre del proveedor para los logs y los motivos de cierre.</summary>
    protected abstract string ProviderName { get; }

    public string? ConversationId { get; protected set; }

    public VoiceAudioFormat InputFormat { get; protected set; } = VoiceAudioFormat.MuLaw8000;

    public VoiceAudioFormat OutputFormat { get; protected set; } = VoiceAudioFormat.MuLaw8000;

    public event Action<byte[]>? AudioReceived;

    public event Action? Interrupted;

    public event Action<string>? UserTranscript;

    public event Action<string>? AgentResponse;

    public event Action<string>? Closed;

    public abstract Task SendUserAudioAsync(byte[] audio, CancellationToken ct);

    public abstract Task SendContextualUpdateAsync(string text, CancellationToken ct);

    protected CancellationToken Lifetime => _cts.Token;

    protected void OnAudio(byte[] audio) => AudioReceived?.Invoke(audio);

    protected void OnInterrupted() => Interrupted?.Invoke();

    protected void OnUserTranscript(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            UserTranscript?.Invoke(text);
        }
    }

    protected void OnAgentResponse(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            AgentResponse?.Invoke(text);
        }
    }

    /// <summary>Arranca el loop de recepción cuando el proveedor ya quedó configurado.</summary>
    protected void StartReceiving() => _receiveLoop = Task.Run(ReceiveLoopAsync);

    protected abstract Task HandleTextAsync(JsonObject message);

    /// <summary>Por defecto los mensajes binarios no se usan (los proveedores JSON mandan el audio en base64).</summary>
    protected virtual void HandleBinary(byte[] data)
    {
    }

    protected Task SendJsonAsync(JsonNode message, CancellationToken ct) =>
        SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, ct);

    protected async Task SendAsync(byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (Socket.State == WebSocketState.Open)
            {
                await Socket.SendAsync(data, type, true, ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Lee un mensaje completo; nulo si el proveedor cerró el socket.</summary>
    protected async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await Socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (result.MessageType, stream.ToArray());
            }
        }
    }

    protected async Task<JsonObject?> ReceiveJsonAsync(CancellationToken ct)
    {
        while (await ReceiveAsync(ct) is { } message)
        {
            if (message.Type == WebSocketMessageType.Text)
            {
                return JsonNode.Parse(message.Data) as JsonObject;
            }

            HandleBinary(message.Data);
        }

        return null;
    }

    private async Task ReceiveLoopAsync()
    {
        var reason = "fin de conversación";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (await ReceiveAsync(_cts.Token) is not { } message)
                {
                    reason = $"{ProviderName} cerró el socket ({Socket.CloseStatus} {Socket.CloseStatusDescription})";
                    break;
                }

                if (message.Type == WebSocketMessageType.Binary)
                {
                    HandleBinary(message.Data);
                }
                else if (JsonNode.Parse(message.Data) is JsonObject json)
                {
                    await HandleTextAsync(json);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            reason = $"error en el socket de {ProviderName}: {ex.Message}";
            Logger.LogError(ex, "Error recibiendo de {Provider}", ProviderName);
        }

        Closed?.Invoke(reason);
    }

    /// <summary>Termina la conversación desde el proveedor (p. ej. un error fatal que no cierra el socket).</summary>
    protected void Close(string reason)
    {
        Closed?.Invoke(reason);
        _cts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // CloseOutputAsync y no CloseAsync: el loop de recepción sigue leyendo en paralelo y dos
        // ReceiveAsync concurrentes no están permitidos.
        if (Socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "llamada terminada", timeout.Token);
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

        Socket.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
