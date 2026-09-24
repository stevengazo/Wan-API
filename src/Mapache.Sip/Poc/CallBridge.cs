using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace Mapache.Sip.Poc;

/// <summary>
/// Puentea una llamada SIP ya contestada con una conversación de ElevenLabs.
/// </summary>
public sealed class CallBridge : IAsyncDisposable
{
    // 20 ms de audio G.711 a 8 kHz = 160 muestras = 160 bytes, el ptime estándar.
    private const int FrameSamples = 160;
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);

    // Umbral de energía para considerar que el llamante está hablando. Empírico; ajustar con llamadas reales.
    private const double VoiceRmsThreshold = 0.02;

    private readonly SIPUserAgent _ua;
    private readonly RTPSession _rtp;
    private readonly ElevenLabsConversation _conversation;
    private readonly TelephonyCodec _codec;
    private readonly PocOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _inbound = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(250) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly JitterBuffer _jitter = new();
    private readonly Lock _outboundLock = new();
    private readonly Queue<byte> _outbound = new();
    private readonly LatencyTracker _latency = new();
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CallBridge(
        SIPUserAgent ua,
        RTPSession rtp,
        ElevenLabsConversation conversation,
        PocOptions options,
        ILogger logger)
    {
        _ua = ua;
        _rtp = rtp;
        _conversation = conversation;
        _options = options;
        _logger = logger;

        var negotiated = rtp.AudioStream.NegotiatedFormat.ToAudioFormat().Codec;
        _codec = negotiated == SIPSorceryMedia.Abstractions.AudioCodecsEnum.PCMA ? TelephonyCodec.Pcma : TelephonyCodec.Pcmu;

        _logger.LogInformation(
            "Bridge iniciado: conversación {ConversationId}, códec {Codec}, ElevenLabs in={In} out={Out}, passthrough in={PassIn} out={PassOut}",
            conversation.ConversationId, _codec, conversation.InputFormat, conversation.OutputFormat,
            AudioConverter.IsPassthrough(_codec, conversation.InputFormat),
            AudioConverter.IsPassthrough(_codec, conversation.OutputFormat));
    }

    /// <summary>Corre hasta que cuelga cualquiera de los dos lados. Devuelve el motivo.</summary>
    public async Task<string> RunAsync()
    {
        _rtp.OnRtpPacketReceived += OnRtpPacketReceived;
        _ua.OnDtmfTone += OnDtmfTone;
        _ua.OnCallHungup += _ => _ended.TrySetResult("el llamante colgó");
        _conversation.AudioReceived += OnAgentAudio;
        _conversation.Interrupted += OnInterrupted;
        _conversation.UserTranscript += text =>
        {
            _latency.OnUserTranscript();
            _logger.LogInformation("Usuario: {Text}", text);
        };
        _conversation.AgentResponse += text => _logger.LogInformation("Agente: {Text}", text);
        _conversation.Closed += reason => _ended.TrySetResult($"ElevenLabs: {reason}");

        var uplink = Task.Run(UplinkLoopAsync);
        var downlink = Task.Run(DownlinkLoopAsync);

        var reason = await _ended.Task;
        await _cts.CancelAsync();
        await Task.WhenAll(uplink, downlink);

        _logger.LogInformation(
            "Llamada terminada ({Reason}). Latencia {Summary}. Jitter buffer: {Lost} perdidos, {Dropped} descartados",
            reason, _latency.Summary(), _jitter.Lost, _jitter.Dropped);
        return reason;
    }

    private void OnRtpPacketReceived(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
    {
        if (mediaType != SDPMediaTypesEnum.audio)
        {
            return;
        }

        // Los paquetes RFC 2833 (DTMF) comparten el stream; SIPSorcery los reporta aparte por OnDtmfTone.
        if (packet.Header.PayloadType != _rtp.AudioStream.NegotiatedFormat.ID)
        {
            return;
        }

        IReadOnlyList<byte[]> ready;
        lock (_jitter)
        {
            ready = _jitter.Push(packet.Header.SequenceNumber, packet.Payload);
        }

        foreach (var payload in ready)
        {
            _inbound.Writer.TryWrite(payload);
        }
    }

    private async Task UplinkLoopAsync()
    {
        try
        {
            await foreach (var g711 in _inbound.Reader.ReadAllAsync(_cts.Token))
            {
                var pcm = AudioConverter.DecodeG711(g711, _codec);
                if (AudioConverter.Rms(pcm) > VoiceRmsThreshold)
                {
                    _latency.OnUserVoice();
                }

                var audio = AudioConverter.TelephonyToElevenLabs(g711, _codec, _conversation.InputFormat);
                await _conversation.SendUserAudioAsync(audio, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando audio a ElevenLabs");
            _ended.TrySetResult("error en uplink");
        }
    }

    private void OnAgentAudio(byte[] audio)
    {
        var g711 = AudioConverter.ElevenLabsToTelephony(audio, _conversation.OutputFormat, _codec);
        lock (_outboundLock)
        {
            if (_outbound.Count == 0)
            {
                var latency = _latency.OnAgentAudio();
                if (latency is not null)
                {
                    _logger.LogInformation("Latencia turno: {Latency}", latency);
                }
            }

            foreach (var b in g711)
            {
                _outbound.Enqueue(b);
            }
        }
    }

    private void OnInterrupted()
    {
        // Barge-in: descartar lo que el agente aún no dijo para que el llamante no lo escuche encima de sí mismo.
        int discarded;
        lock (_outboundLock)
        {
            discarded = _outbound.Count;
            _outbound.Clear();
        }

        _logger.LogInformation("Interrupción: descartados {Ms} ms de audio del agente", discarded / 8);
    }

    /// <summary>
    /// Envía un frame de 20 ms en cada tick, con silencio si no hay audio del agente. El flujo continuo
    /// mantiene abierto el binding NAT del RTP y evita que el teléfono detecte timeout de media.
    /// </summary>
    private async Task DownlinkLoopAsync()
    {
        var silence = _codec == TelephonyCodec.Pcmu ? (byte)0xFF : (byte)0xD5;
        var frame = new byte[FrameSamples];
        var clock = Stopwatch.StartNew();
        long sent = 0;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Se recupera por reloj y no por ticks: en Windows Task.Delay tiene granularidad de ~15 ms
                // y un PeriodicTimer de 20 ms derivaría.
                var due = clock.Elapsed.Ticks / FrameDuration.Ticks;
                if (due - sent > 5)
                {
                    // Tras una pausa larga (GC, CPU) no se manda una ráfaga: el teléfono la descartaría igual.
                    sent = due - 1;
                }

                for (; sent < due; sent++)
                {
                    lock (_outboundLock)
                    {
                        for (var i = 0; i < frame.Length; i++)
                        {
                            frame[i] = _outbound.Count > 0 ? _outbound.Dequeue() : silence;
                        }
                    }

                    _rtp.SendAudio(FrameSamples, frame);
                }

                await Task.Delay(5, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnDtmfTone(byte key, int duration)
    {
        var digit = key switch
        {
            <= 9 => (char)('0' + key),
            10 => '*',
            11 => '#',
            _ => '?',
        };
        _logger.LogInformation("DTMF {Digit} ({Duration} ms)", digit, duration);

        try
        {
            if (digit == _options.TransferDtmfDigit && !string.IsNullOrWhiteSpace(_options.TransferTarget))
            {
                await TransferAsync(_options.TransferTarget);
                return;
            }

            await _conversation.SendContextualUpdateAsync($"El usuario marcó la tecla {digit} en el teclado del teléfono.", _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando DTMF {Digit}", digit);
        }
    }

    /// <summary>Transferencia ciega con REFER (prueba D2).</summary>
    private async Task TransferAsync(string target)
    {
        var destination = target.Contains('@')
            ? SIPURI.ParseSIPURIRelaxed(target)
            : SIPURI.ParseSIPURIRelaxed($"{target}@{_options.Sip.Server}");

        _logger.LogInformation("REFER hacia {Destination}", destination);
        var accepted = await _ua.BlindTransfer(destination, TimeSpan.FromSeconds(10), _cts.Token);
        _logger.LogInformation("REFER {Result}", accepted ? "aceptado por el proveedor" : "rechazado o sin respuesta");
        if (accepted)
        {
            _ended.TrySetResult($"transferida a {destination}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _rtp.OnRtpPacketReceived -= OnRtpPacketReceived;
        _ua.OnDtmfTone -= OnDtmfTone;
        if (_ua.IsCallActive)
        {
            _ua.Hangup();
        }

        await _conversation.DisposeAsync();
        _rtp.Close("llamada terminada");
        _cts.Dispose();
    }
}
