namespace Mapache.Sip.Poc;

public sealed class PocOptions
{
    public const string SectionName = "Sip:Poc";

    public bool Enabled { get; set; }

    public SipAccountOptions Sip { get; set; } = new();

    public ElevenLabsOptions ElevenLabs { get; set; } = new();

    /// <summary>Destino de REFER al marcar <see cref="TransferDtmfDigit"/> (prueba D2). Vacío = deshabilitado.</summary>
    public string? TransferTarget { get; set; }

    public char TransferDtmfDigit { get; set; } = '9';
}

public sealed class SipAccountOptions
{
    public string Server { get; set; } = "";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public int ExpirySeconds { get; set; } = 120;

    /// <summary>Puerto UDP local para señalización SIP. 0 = lo elige el sistema.</summary>
    public int ListenPort { get; set; } = 5060;

    /// <summary>IP pública a anunciar en el SDP cuando el host está detrás de NAT.</summary>
    public string? PublicIpAddress { get; set; }
}

public sealed class ElevenLabsOptions
{
    public string ApiKey { get; set; } = "";

    public string AgentId { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.elevenlabs.io";
}
