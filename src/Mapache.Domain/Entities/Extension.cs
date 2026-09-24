namespace Mapache.Domain.Entities;

/// <summary>Cuenta SIP que el bot registra para atender llamadas.</summary>
public class Extension
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Guid ProviderId { get; set; }

    public Provider? Provider { get; set; }

    public required string SipUsername { get; set; }

    /// <summary>Contraseña SIP cifrada con Data Protection; nunca se expone por la API.</summary>
    public required string SipPasswordEncrypted { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
