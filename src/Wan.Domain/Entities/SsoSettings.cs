namespace Mapache.Domain.Entities;

/// <summary>Inicio de sesión con Google y Microsoft. Hay una sola fila; los secretos van cifrados.</summary>
public class SsoSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public bool GoogleEnabled { get; set; }

    public string? GoogleClientId { get; set; }

    public string? GoogleClientSecretEncrypted { get; set; }

    public bool MicrosoftEnabled { get; set; }

    public string? MicrosoftClientId { get; set; }

    public string? MicrosoftClientSecretEncrypted { get; set; }

    /// <summary>
    /// <c>organizations</c> (cualquier cuenta de trabajo), <c>common</c> (también personales) o el ID del inquilino
    /// de la empresa para aceptar solo sus cuentas.
    /// </summary>
    public string MicrosoftTenant { get; set; } = "organizations";

    /// <summary>Crear la cuenta (rol Operador) la primera vez que alguien entra con un proveedor.</summary>
    public bool AllowRegistration { get; set; }

    /// <summary>Dominios de correo aceptados al registrarse ("miempresa.com, filial.com"). Vacío = cualquiera.</summary>
    public string? AllowedDomains { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
