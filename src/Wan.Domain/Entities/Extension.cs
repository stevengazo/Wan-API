namespace Wan.Domain.Entities;

public enum SipTransport
{
    Udp,
    Tcp,
    Tls,
}

/// <summary>Cifrado del audio (SRTP).</summary>
public enum MediaEncryption
{
    Disabled,

    /// <summary>Ofrece SRTP pero acepta RTP sin cifrar si el otro extremo no lo soporta.</summary>
    Optional,
    Mandatory,
}

/// <summary>Cómo se envían y reciben los tonos DTMF.</summary>
public enum DtmfMode
{
    /// <summary>Eventos RTP (RFC 2833 / 4733): lo más compatible.</summary>
    Rfc2833,
    SipInfo,
    Inband,
}

/// <summary>Quién contesta las llamadas entrantes de la cuenta.</summary>
public enum AnswerMode
{
    Bot,
    Human,

    /// <summary>Atiende el bot y transfiere a una persona del panel cuando hace falta.</summary>
    BotWithHandoff,
}

/// <summary>
/// Cuenta SIP genérica, con los mismos datos que pide un softphone como MicroSIP.
/// Sirve para cualquier servidor SIP; no hay proveedores fijos.
/// </summary>
public class Extension
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Registrar SIP: <c>host</c> o <c>host:puerto</c>.</summary>
    public required string SipServer { get; set; }

    /// <summary>Registrar de respaldo: se usa si el principal deja de responder.</summary>
    public string? SecondarySipServer { get; set; }

    /// <summary>Proxy saliente opcional; vacío = se usa <see cref="SipServer"/>.</summary>
    public string? SipProxy { get; set; }

    public required string SipUsername { get; set; }

    /// <summary>Dominio del AOR (<c>usuario@dominio</c>); vacío = el host de <see cref="SipServer"/>.</summary>
    public string? SipDomain { get; set; }

    /// <summary>Usuario de autenticación ("Login" en MicroSIP); vacío = <see cref="SipUsername"/>.</summary>
    public string? AuthUsername { get; set; }

    /// <summary>Contraseña SIP cifrada con Data Protection; nunca se expone por la API.</summary>
    public required string SipPasswordEncrypted { get; set; }

    /// <summary>Nombre que ve el otro extremo en las llamadas salientes.</summary>
    public string? DisplayName { get; set; }

    public SipTransport Transport { get; set; } = SipTransport.Udp;

    /// <summary>IP pública a anunciar en SIP/SDP cuando el host está detrás de NAT.</summary>
    public string? PublicAddress { get; set; }

    /// <summary>Servidor STUN (<c>host[:puerto]</c>) para descubrir la IP pública.</summary>
    public string? StunServer { get; set; }

    public int RegisterExpirySeconds { get; set; } = 300;

    /// <summary>Espera antes de reintentar un REGISTER fallido.</summary>
    public int RegisterRetrySeconds { get; set; } = 30;

    /// <summary>Intervalo de keepalive para mantener abierto el NAT; 0 = deshabilitado.</summary>
    public int KeepAliveSeconds { get; set; } = 15;

    /// <summary>Puerto local de señalización SIP; 0 = lo elige el sistema.</summary>
    public int LocalPort { get; set; }

    /// <summary>Usar la IP y el puerto con que el servidor nos ve (rport) en lugar de los locales.</summary>
    public bool AllowIpRewrite { get; set; } = true;

    /// <summary>ICE para atravesar NAT en el audio.</summary>
    public bool UseIce { get; set; }

    public MediaEncryption MediaEncryption { get; set; } = MediaEncryption.Disabled;

    /// <summary>Códecs habilitados en orden de preferencia (ver <see cref="SupportedCodecs"/>).</summary>
    public List<string> Codecs { get; set; } = ["PCMU", "PCMA"];

    public DtmfMode DtmfMode { get; set; } = DtmfMode.Rfc2833;

    /// <summary>Refresco de sesión (RFC 4028) para detectar llamadas colgadas; 0 = deshabilitado.</summary>
    public int SessionTimerSeconds { get; set; } = 1800;

    /// <summary>Llamadas simultáneas que atiende la cuenta; 0 = sin límite.</summary>
    public int MaxConcurrentCalls { get; set; }

    /// <summary>Número del buzón de voz del servidor.</summary>
    public string? VoicemailNumber { get; set; }

    /// <summary>Prefijo que se antepone a los números marcados (p. ej. para salir a la red pública).</summary>
    public string? DialPrefix { get; set; }

    /// <summary>Ocultar el número en las llamadas salientes (anónimo).</summary>
    public bool HideCallerId { get; set; }

    /// <summary>PCMU primero: con <c>ulaw_8000</c> en ElevenLabs el audio pasa sin transcodificar.</summary>
    public static readonly IReadOnlyList<string> SupportedCodecs = ["PCMU", "PCMA", "G722", "G729", "opus"];

    public AnswerMode AnswerMode { get; set; } = AnswerMode.Bot;

    /// <summary>Horario de atención: fuera de él o en el descanso no se transfieren llamadas.</summary>
    public BusinessHours BusinessHours { get; set; } = new();

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
