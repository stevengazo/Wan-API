namespace Wan.Domain.Entities;

/// <summary>
/// Registro de auditoría: quién hizo qué, sobre qué y desde dónde. Solo se agregan filas; la API no permite
/// editarlas ni borrarlas.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Nulo para acciones del sistema (borrado automático) o de enlaces públicos.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Correo en el momento del evento: sigue legible aunque el usuario se borre.</summary>
    public string? UserEmail { get; set; }

    /// <summary>Código de la acción: <c>recording.played</c>, <c>user.role_changed</c>...</summary>
    public required string Action { get; set; }

    /// <summary>Descripción legible del objeto ("Grabación del 25/9 14:03", "ana@empresa.com").</summary>
    public string? Target { get; set; }

    public string? TargetId { get; set; }

    public string? IpAddress { get; set; }

    public string? Details { get; set; }
}
