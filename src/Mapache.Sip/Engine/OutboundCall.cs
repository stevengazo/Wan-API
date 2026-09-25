using System.Net;
using Mapache.Domain.Entities;
using Mapache.Sip.Media;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace Mapache.Sip.Engine;

/// <summary>
/// Llamada saliente del teléfono web: marca con las credenciales de la cuenta y pasa el audio tal cual
/// entre el RTP (G.711) y el navegador (PCM), sin bot en el medio.
/// </summary>
internal sealed class OutboundCall : IPhoneCall
{
    // Pasado este tiempo sonando se cancela, como hace un teléfono físico.
    private const int RingTimeoutSeconds = 60;

    private readonly SipAccountRuntime _runtime;
    private readonly SIPUserAgent _ua;
    private readonly RTPSession _rtp;
    private readonly SIPCallDescriptor _descriptor;
    private readonly string _number;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _answered;
    private TelephonyCodec _codec = TelephonyCodec.Pcmu;

    public OutboundCall(SipAccountRuntime runtime, SIPUserAgent ua, RTPSession rtp, SIPCallDescriptor descriptor, string number, ILogger logger)
    {
        _runtime = runtime;
        _ua = ua;
        _rtp = rtp;
        _descriptor = descriptor;
        _number = number;
        _logger = logger;
    }

    public event Action<PhoneCallState, string?>? StateChanged;

    public event Action<short[]>? AudioReceived;

    public async Task<string> RunAsync(CancellationToken ct)
    {
        var callId = await _runtime.OutboundCallStartedAsync(_number);
        var status = CallStatus.Failed;
        var reason = "error";
        using var scope = _logger.BeginScope("Llamada saliente {CallId} a {Number}", callId, _number);

        try
        {
            _ua.ClientCallRinging += (_, _) => StateChanged?.Invoke(PhoneCallState.Ringing, null);
            _ua.ClientCallFailed += (_, error, response) => _ended.TrySetResult(Describe(error, response));
            _ua.OnCallHungup += _ => _ended.TrySetResult("el otro lado colgó");
            _rtp.OnRtpPacketReceived += OnRtpPacketReceived;
            await using var registration = ct.Register(Hangup);

            StateChanged?.Invoke(PhoneCallState.Calling, null);
            _logger.LogInformation("Marcando {Uri}", _descriptor.Uri);

            if (!await _ua.Call(_descriptor, _rtp, RingTimeoutSeconds))
            {
                reason = _ended.Task.IsCompleted ? await _ended.Task : "no contestó";
                status = CallStatus.Missed;
                return reason;
            }

            var negotiated = _rtp.AudioStream.NegotiatedFormat.ToAudioFormat().Codec;
            _codec = negotiated == SIPSorceryMedia.Abstractions.AudioCodecsEnum.PCMA ? TelephonyCodec.Pcma : TelephonyCodec.Pcmu;
            _answered = true;
            await _runtime.Events.CallAnsweredAsync(callId, null);
            StateChanged?.Invoke(PhoneCallState.Answered, null);
            _logger.LogInformation("Contestada, códec {Codec}", _codec);

            reason = await _ended.Task;
            status = CallStatus.Completed;
            return reason;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en la llamada saliente");
            reason = ex.Message;
            return reason;
        }
        finally
        {
            if (_ua.IsCallActive)
            {
                _ua.Hangup();
            }

            _rtp.Close(reason);
            StateChanged?.Invoke(PhoneCallState.Ended, reason);
            await _runtime.OutboundCallEndedAsync(callId, status, reason);
        }
    }

    public void SendAudio(ReadOnlySpan<short> pcm)
    {
        if (!_answered || pcm.IsEmpty)
        {
            return;
        }

        // La duración va en unidades del reloj RTP, que en G.711 es una por muestra.
        _rtp.SendAudio((uint)pcm.Length, AudioConverter.EncodeG711(pcm, _codec));
    }

    public void SendDtmf(char digit)
    {
        byte? tone = digit switch
        {
            >= '0' and <= '9' => (byte)(digit - '0'),
            '*' => 10,
            '#' => 11,
            _ => null,
        };
        if (_answered && tone is { } value)
        {
            _ = _ua.SendDtmf(value);
        }
    }

    public void Hangup()
    {
        // El motivo primero: Hangup dispara OnCallHungup en el acto y ganaría "el otro lado colgó".
        if (_answered)
        {
            _ended.TrySetResult("colgaste");
            _ua.Hangup();
        }
        else
        {
            _ended.TrySetResult("cancelada antes de contestar");
            _ua.Cancel();
        }
    }

    private void OnRtpPacketReceived(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
    {
        if (mediaType != SDPMediaTypesEnum.audio)
        {
            return;
        }

        // Por payload type y no por el códec negociado: el audio temprano (183) llega antes de contestar.
        TelephonyCodec? codec = packet.Header.PayloadType switch
        {
            0 => TelephonyCodec.Pcmu,
            8 => TelephonyCodec.Pcma,
            _ => null,
        };
        if (codec is { } value)
        {
            AudioReceived?.Invoke(AudioConverter.DecodeG711(packet.Payload, value));
        }
    }

    private static string Describe(string error, SIPResponse? response) => response?.Status switch
    {
        SIPResponseStatusCodesEnum.BusyHere or SIPResponseStatusCodesEnum.BusyEverywhere => "ocupado",
        SIPResponseStatusCodesEnum.NotFound => "el número no existe",
        SIPResponseStatusCodesEnum.TemporarilyUnavailable => "no disponible",
        SIPResponseStatusCodesEnum.Decline => "rechazó la llamada",
        SIPResponseStatusCodesEnum.Forbidden => "la central no permite esta llamada (403)",
        SIPResponseStatusCodesEnum.RequestTerminated => "cancelada antes de contestar",
        SIPResponseStatusCodesEnum.RequestTimeout => "no contestó",
        { } status => $"{(int)status} {response!.ReasonPhrase}",
        null => error,
    };
}
