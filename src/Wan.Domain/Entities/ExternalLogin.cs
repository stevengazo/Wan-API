namespace Mapache.Domain.Entities;

public enum ExternalProvider
{
    Google,
    Microsoft,
}

/// <summary>Cuenta de Google o Microsoft vinculada a un usuario del panel.</summary>
public class ExternalLogin
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public ExternalProvider Provider { get; set; }

    /// <summary>Claim <c>sub</c> del proveedor: estable para la cuenta, a diferencia del correo.</summary>
    public required string Subject { get; set; }

    /// <summary>Solo para mostrar en el perfil; nunca se usa para identificar la cuenta.</summary>
    public string? Email { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
