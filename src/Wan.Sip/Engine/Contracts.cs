using Wan.Domain.Entities;
using Wan.Sip.Media;

namespace Wan.Sip.Engine;

/// <summary>Cuenta SIP lista para registrar: la configuración del panel con la contraseña ya descifrada.</summary>
public sealed record SipAccount(Extension Extension, string Password);

public enum SipRegistrationState
{
    Registering,
    Registered,
    Failed,
}

/// <summary>Estado del registro de una cuenta, tal como lo ve el panel.</summary>
public sealed record SipRegistrationStatus(
    Guid ExtensionId,
    SipRegistrationState State,
    string Server,
    string? Error,
    int ActiveCalls,
    DateTimeOffset Since);

/// <summary>Cuentas habilitadas (lo implementa la API con la base).</summary>
public interface ISipAccountSource
{
    Task<IReadOnlyList<SipAccount>> GetEnabledAsync(CancellationToken ct);
}

/// <summary>Proveedor de voz configurado en el panel (ElevenLabs, OpenAI Realtime, Deepgram); nulo si falta.</summary>
public interface IVoiceAgentSource
{
    Task<VoiceProviderOptions?> GetAsync(CancellationToken ct);
}

/// <summary>Lo que pasa en el motor, para persistirlo y avisar al panel (lo implementa la API).</summary>
public interface ISipEventSink
{
    Task RegistrationChangedAsync(SipRegistrationStatus status);

    /// <summary>Devuelve el id de la llamada creada.</summary>
    Task<Guid> CallStartedAsync(Guid extensionId, string callerNumber, CallDirection direction);

    Task CallAnsweredAsync(Guid callId, string? conversationId);

    Task TranscriptAsync(Guid callId, TranscriptSpeaker speaker, string text);

    Task CallEndedAsync(Guid callId, CallStatus status, string reason);

    /// <summary>Si hay que grabar las llamadas (Configuración → Llamadas).</summary>
    Task<bool> RecordingEnabledAsync();

    /// <summary>
    /// Grabación de una llamada terminada: WAV PCM 16 bits 8 kHz mono en un archivo temporal que pasa a ser de
    /// quien lo recibe (lo guarda donde corresponda y lo borra).
    /// </summary>
    Task RecordingCompletedAsync(Guid callId, string conversationId, string wavPath, TimeSpan duration);
}

/// <summary>Control del motor desde la API.</summary>
public interface ISipEngine
{
    /// <summary>Vuelve a leer las cuentas (se llama cuando se edita una extensión).</summary>
    void RequestReload();

    IReadOnlyList<SipRegistrationStatus> GetStatuses();

    /// <summary>
    /// Prepara una llamada saliente desde una extensión activa (el teléfono web del panel). No marca hasta
    /// <see cref="IPhoneCall.RunAsync"/>, para que quien la usa se suscriba antes a los eventos.
    /// </summary>
    /// <exception cref="InvalidOperationException">La extensión no está activa o no admite más llamadas.</exception>
    IPhoneCall PlaceCall(Guid extensionId, string destination);

    /// <summary>Prepara la toma de una llamada entrante que atiende el bot; el bot la suelta al correr la llamada.</summary>
    /// <exception cref="InvalidOperationException">La llamada ya terminó o no la atiende el bot.</exception>
    IPhoneCall TakeCall(Guid callId);

    /// <summary>
    /// Transfiere (REFER) una llamada que atiende el bot. Devuelve el resultado para el modelo; no transfiere
    /// fuera del horario de atención de la extensión.
    /// </summary>
    Task<string> TransferAsync(Guid callId, string target);

    /// <summary>Llamada saliente atendida por el bot, para campañas. Corre hasta que la llamada termina.</summary>
    /// <exception cref="InvalidOperationException">La extensión no está activa, no admite más llamadas o falta la voz.</exception>
    Task<BotCallResult> DialBotAsync(BotCallRequest request, CancellationToken ct);
}

/// <summary>Contacto de una campaña a llamar; <paramref name="OpeningLine"/> ya con las variables reemplazadas.</summary>
public sealed record BotCallRequest(Guid ExtensionId, string Destination, Guid CampaignId, Guid ContactId, string? OpeningLine);

/// <summary>Cómo terminó una llamada de campaña: si contestó, si daba ocupado y el motivo.</summary>
public sealed record BotCallResult(Guid? CallId, bool Answered, bool Busy, string Reason);

public enum PhoneCallState
{
    Calling,
    Ringing,
    Answered,
    Ended,
}

/// <summary>Llamada saliente atendida por una persona desde el navegador. El audio va en PCM de 16 bits a 8 kHz.</summary>
public interface IPhoneCall
{
    /// <summary>Estado y, al terminar, el motivo.</summary>
    event Action<PhoneCallState, string?>? StateChanged;

    /// <summary>Audio del otro lado (también el audio temprano: tono o mensajes de la central).</summary>
    event Action<short[]>? AudioReceived;

    /// <summary>Marca y espera a que termine. Devuelve el motivo del fin.</summary>
    Task<string> RunAsync(CancellationToken ct);

    void SendAudio(ReadOnlySpan<short> pcm);

    /// <summary>Tono DTMF por RFC 2833: 0-9, * o #.</summary>
    void SendDtmf(char digit);

    /// <summary>Cuelga, o cancela si todavía está sonando.</summary>
    void Hangup();
}

public sealed class SipEngineOptions
{
    public const string SectionName = "Sip";

    /// <summary>Con false no se registra ninguna cuenta (útil en desarrollo sin red SIP).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rango de puertos UDP para el audio (RTP); 0 = los elige el sistema. En Docker debe coincidir con los publicados.</summary>
    public int RtpPortStart { get; set; }

    public int RtpPortEnd { get; set; }
}
