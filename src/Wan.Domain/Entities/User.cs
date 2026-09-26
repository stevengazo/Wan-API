namespace Wan.Domain.Entities;

public enum UserRole
{
    Admin,
    Operator,
}

public class User
{
    public Guid Id { get; set; }

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Vacío en cuentas creadas con Google o Microsoft: solo entran por ahí hasta que se les asigne una.</summary>
    public string PasswordHash { get; set; } = "";

    public UserRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Semilla TOTP protegida con Data Protection. Existe desde que se inicia la configuración.</summary>
    public string? MfaSecretEncrypted { get; set; }

    /// <summary>Se activa recién cuando el usuario confirma un código de su app autenticadora.</summary>
    public bool MfaEnabled { get; set; }

    /// <summary>SHA-256 de los códigos de recuperación sin usar; cada uno sirve una sola vez.</summary>
    public List<string> MfaRecoveryCodeHashes { get; set; } = [];

    /// <summary>Último intervalo TOTP aceptado: impide reutilizar un código dentro de su ventana.</summary>
    public long? MfaLastStep { get; set; }

    public List<ExternalLogin> ExternalLogins { get; set; } = [];
}
