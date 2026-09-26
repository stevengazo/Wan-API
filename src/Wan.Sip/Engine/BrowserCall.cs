using System.Net;
using Wan.Sip.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;

namespace Wan.Sip.Engine;

/// <summary>
/// Base de las llamadas atendidas desde el navegador: pasa el audio tal cual entre el RTP (G.711) y el
/// navegador (PCM), sin bot en el medio. Las subclases deciden cómo se conecta y cómo se cuelga.
/// </summary>
internal abstract class BrowserCall : IPhoneCall
{
    private volatile bool _connected;
    private TelephonyCodec _codec = TelephonyCodec.Pcmu;

    public event Action<PhoneCallState, string?>? StateChanged;

    public event Action<short[]>? AudioReceived;

    protected SIPUserAgent? Ua { get; private set; }

    protected RTPSession? Rtp { get; private set; }

    protected bool Connected => _connected;

    /// <summary>Grabación en curso de la llamada, si la hay (se hereda del bot al tomarla).</summary>
    protected CallRecorder? Recorder { get; set; }

    public abstract Task<string> RunAsync(CancellationToken ct);

    public abstract void Hangup();

    public void SendAudio(ReadOnlySpan<short> pcm)
    {
        if (!_connected || pcm.IsEmpty || Rtp is null)
        {
            return;
        }

        // La duración va en unidades del reloj RTP, que en G.711 es una por muestra.
        Rtp.SendAudio((uint)pcm.Length, AudioConverter.EncodeG711(pcm, _codec));
        Recorder?.AddOther(pcm);
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
        if (_connected && tone is { } value && Ua is not null)
        {
            _ = Ua.SendDtmf(value);
        }
    }

    /// <summary>Engancha la llamada SIP; el audio que llega se reenvía desde ya (también el audio temprano).</summary>
    protected void Attach(SIPUserAgent ua, RTPSession rtp)
    {
        Ua = ua;
        Rtp = rtp;
        rtp.OnRtpPacketReceived += OnRtpPacketReceived;
    }

    /// <summary>Contestada: desde aquí se envía el audio del navegador con el códec negociado.</summary>
    protected void MarkConnected()
    {
        var negotiated = Rtp!.AudioStream.NegotiatedFormat.ToAudioFormat().Codec;
        _codec = negotiated == SIPSorceryMedia.Abstractions.AudioCodecsEnum.PCMA ? TelephonyCodec.Pcma : TelephonyCodec.Pcmu;
        _connected = true;
        Raise(PhoneCallState.Answered);
    }

    protected void Raise(PhoneCallState state, string? reason = null) => StateChanged?.Invoke(state, reason);

    protected void Detach(string reason)
    {
        if (Rtp is not null)
        {
            Rtp.OnRtpPacketReceived -= OnRtpPacketReceived;
        }

        if (Ua?.IsCallActive == true)
        {
            Ua.Hangup();
        }

        Rtp?.Close(reason);
        _connected = false;
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
            var pcm = AudioConverter.DecodeG711(packet.Payload, value);
            Recorder?.AddCaller(pcm);
            AudioReceived?.Invoke(pcm);
        }
    }
}
