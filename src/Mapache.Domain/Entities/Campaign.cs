namespace Mapache.Domain.Entities;

public enum CampaignStatus
{
    Draft,
    Running,
    Paused,
    Completed,
}

/// <summary>Campaña de llamadas salientes que hace el bot a una lista de contactos.</summary>
public class Campaign
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Extensión desde la que se llama (su cuenta SIP, sus límites y su horario).</summary>
    public Guid ExtensionId { get; set; }

    public Extension? Extension { get; set; }

    /// <summary>Objetivo y guion: qué debe lograr el bot en cada llamada. Admite <c>{{variable}}</c> del contacto.</summary>
    public required string Instructions { get; set; }

    /// <summary>Primera frase al contestar ("Hola {{nombre}}, te llamo de..."); vacío = la decide el bot.</summary>
    public string? OpeningLine { get; set; }

    /// <summary>Resultados posibles que registra el bot ("Interesado", "No interesado"...).</summary>
    public List<string> Outcomes { get; set; } = [];

    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;

    public int MaxConcurrentCalls { get; set; } = 1;

    public int MaxAttempts { get; set; } = 2;

    /// <summary>Espera antes de reintentar a quien no contestó o estaba ocupado.</summary>
    public int RetryMinutes { get; set; } = 60;

    /// <summary>Solo marca dentro del horario de atención de la extensión.</summary>
    public bool RespectBusinessHours { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public enum CampaignContactStatus
{
    Pending,

    /// <summary>Con una llamada en curso.</summary>
    Calling,

    /// <summary>Contestó y habló con el bot.</summary>
    Completed,

    /// <summary>Se agotaron los intentos sin que contestara.</summary>
    NoAnswer,

    /// <summary>Se agotaron los intentos y la línea daba ocupado.</summary>
    Busy,

    /// <summary>La llamada no se pudo hacer (número inválido, rechazo de la central...).</summary>
    Failed,
}

public class CampaignContact
{
    public Guid Id { get; set; }

    public Guid CampaignId { get; set; }

    public Campaign? Campaign { get; set; }

    public required string PhoneNumber { get; set; }

    public string? Name { get; set; }

    /// <summary>Datos para el guion ({{monto}}, {{fecha}}...), del CSV importado.</summary>
    public Dictionary<string, string> Variables { get; set; } = [];

    public CampaignContactStatus Status { get; set; } = CampaignContactStatus.Pending;

    public int Attempts { get; set; }

    /// <summary>Cuándo se puede volver a llamar; nulo = cuanto antes.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    public Guid? LastCallId { get; set; }

    /// <summary>Motivo técnico del último intento ("no contestó", "ocupado"...).</summary>
    public string? LastResult { get; set; }

    /// <summary>Resultado que registró el bot (uno de <see cref="Campaign.Outcomes"/>).</summary>
    public string? Outcome { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
