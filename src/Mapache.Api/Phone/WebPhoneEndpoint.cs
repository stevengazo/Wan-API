using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Mapache.Sip.Engine;

namespace Mapache.Api.Phone;

/// <summary>
/// Teléfono web del panel: un WebSocket por llamada. Binario = audio PCM de 16 bits a 8 kHz en ambos
/// sentidos; texto = JSON de control (<c>hangup</c>, <c>dtmf</c>) y de estado (<c>state</c>).
/// Va bajo /hubs para usar el mismo proxy WebSocket y el mismo token por query string que SignalR.
/// </summary>
public static partial class WebPhoneEndpoint
{
    public const string Path = "/hubs/phone";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapWebPhone(this WebApplication app) =>
        app.Map(Path, HandleAsync).RequireAuthorization();

    private static async Task HandleAsync(HttpContext http, ISipEngine engine, ILoggerFactory loggers)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var logger = loggers.CreateLogger("Mapache.Api.Phone");
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var ct = http.RequestAborted;

        // El motivo va por el propio socket: si se rechazara el handshake, el navegador no mostraría el cuerpo.
        var to = http.Request.Query["to"].ToString().Trim();
        if (!Guid.TryParse(http.Request.Query["extensionId"], out var extensionId) || !DestinationPattern().IsMatch(to))
        {
            await EndAsync(socket, "Número o extensión inválidos.", ct);
            return;
        }

        IPhoneCall call;
        try
        {
            call = engine.PlaceCall(extensionId, to);
        }
        catch (InvalidOperationException ex)
        {
            await EndAsync(socket, ex.Message, ct);
            return;
        }

        logger.LogInformation("{User} llama a {To} desde el teléfono web", http.User.Identity?.Name, to);

        // Los eventos llegan desde hilos de SIPSorcery; un solo escritor ordena los envíos al socket.
        var outbox = Channel.CreateBounded<(byte[] Data, WebSocketMessageType Type)>(
            new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        call.StateChanged += (state, reason) => outbox.Writer.TryWrite((StateMessage(state, reason), WebSocketMessageType.Text));
        call.AudioReceived += pcm => outbox.Writer.TryWrite((MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray(), WebSocketMessageType.Binary));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sender = SendLoopAsync(socket, outbox.Reader, stop.Token);
        var run = call.RunAsync(stop.Token);
        var receiver = ReceiveLoopAsync(socket, call, stop.Token);

        await Task.WhenAny(run, receiver);
        call.Hangup();
        await run;
        outbox.Writer.TryComplete();
        await sender;
        await stop.CancelAsync();
        await CloseAsync(socket);
    }

    private static async Task ReceiveLoopAsync(WebSocket socket, IPhoneCall call, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
                    length += result.Count;
                }
                while (!result.EndOfMessage && length < buffer.Length);

                switch (result.MessageType)
                {
                    case WebSocketMessageType.Close:
                        return;
                    case WebSocketMessageType.Binary:
                        call.SendAudio(MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, length & ~1)));
                        break;
                    case WebSocketMessageType.Text:
                        var command = JsonSerializer.Deserialize<PhoneCommand>(buffer.AsSpan(0, length), Json);
                        if (command?.Type == "hangup")
                        {
                            return;
                        }

                        if (command?.Type == "dtmf" && command.Digit is { Length: 1 } digit)
                        {
                            call.SendDtmf(digit[0]);
                        }

                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException)
        {
            // Pestaña cerrada o mensaje inválido: se cuelga igual.
        }
    }

    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<(byte[] Data, WebSocketMessageType Type)> outbox, CancellationToken ct)
    {
        try
        {
            await foreach (var (data, type) in outbox.ReadAllAsync(ct))
            {
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }

                await socket.SendAsync(data, type, true, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }

    private static async Task EndAsync(WebSocket socket, string reason, CancellationToken ct)
    {
        await socket.SendAsync(StateMessage(PhoneCallState.Ended, reason), WebSocketMessageType.Text, true, ct);
        await CloseAsync(socket);
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }
    }

    private static byte[] StateMessage(PhoneCallState state, string? reason) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "state", state = state.ToString().ToLowerInvariant(), reason }, Json));

    // Número con prefijos y códigos de servicio (*, #, +) o un URI SIP.
    [GeneratedRegex(@"^(sip:)?[\w.+*#@:-]{1,128}$")]
    private static partial Regex DestinationPattern();

    private sealed record PhoneCommand(string Type, string? Digit);
}
