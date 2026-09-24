namespace Mapache.Domain.Entities;

public enum ProviderType
{
    Zadarma,
    Twilio,
    Calmyway,
}

/// <summary>Proveedor SIP donde se registran las extensiones.</summary>
public class Provider
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public ProviderType Type { get; set; }

    /// <summary>Registrar SIP, p. ej. <c>pbx.zadarma.com</c>.</summary>
    public required string SipServer { get; set; }

    public List<Extension> Extensions { get; set; } = [];
}
