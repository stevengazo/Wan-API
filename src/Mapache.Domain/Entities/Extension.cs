namespace Mapache.Domain.Entities;

public enum SipTransport
{
    Udp,
    Tcp,
    Tls,
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

    /// <summary>Intervalo de keepalive para mantener abierto el NAT; 0 = deshabilitado.</summary>
    public int KeepAliveSeconds { get; set; } = 15;

    public AnswerMode AnswerMode { get; set; } = AnswerMode.Bot;

    /// <summary>Proveedor del LLM del bot; nulo = el de <c>Llm:DefaultProvider</c>.</summary>
    public LlmProvider? LlmProvider { get; set; }

    /// <summary>Modelo del proveedor; vacío = el modelo por defecto configurado para ese proveedor.</summary>
    public string? LlmModel { get; set; }

    /// <summary>Instrucciones del bot para esta cuenta; vacío = las del agente de ElevenLabs.</summary>
    public string? SystemPrompt { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
