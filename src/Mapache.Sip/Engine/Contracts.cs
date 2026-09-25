using Mapache.Domain.Entities;
using Mapache.Sip.Media;

namespace Mapache.Sip.Engine;

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

/// <summary>Agente de ElevenLabs configurado en el panel; nulo si falta.</summary>
public interface IVoiceAgentSource
{
    Task<ElevenLabsOptions?> GetAsync(CancellationToken ct);
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
}

/// <summary>Control del motor desde la API.</summary>
public interface ISipEngine
{
    /// <summary>Vuelve a leer las cuentas (se llama cuando se edita una extensión).</summary>
    void RequestReload();

    IReadOnlyList<SipRegistrationStatus> GetStatuses();
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
