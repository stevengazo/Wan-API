namespace Wan.Domain.Entities;

public enum BusinessHoursState
{
    Open,

    /// <summary>Dentro del día, en el descanso (almuerzo).</summary>
    OnBreak,

    Closed,
}

/// <summary>Horario de un día de atención, con un descanso opcional en el medio.</summary>
public class BusinessDay
{
    public DayOfWeek Day { get; set; }

    public TimeOnly Opens { get; set; } = new(8, 0);

    public TimeOnly Closes { get; set; } = new(17, 0);

    public TimeOnly? BreakStarts { get; set; }

    public TimeOnly? BreakEnds { get; set; }
}

/// <summary>
/// Horario de atención de una extensión. Fuera de horario o en el descanso el bot no transfiere llamadas
/// y dice el mensaje correspondiente. Desactivado = siempre abierto.
/// </summary>
public class BusinessHours
{
    public bool Enabled { get; set; }

    /// <summary>Zona horaria IANA (America/Costa_Rica): el horario se evalúa en la hora local de la oficina.</summary>
    public string TimeZone { get; set; } = "America/Costa_Rica";

    /// <summary>Solo los días que se atiende; los que faltan están cerrados.</summary>
    public List<BusinessDay> Days { get; set; } = [];

    /// <summary>Lo que dice el bot fuera de horario ("Atendemos de lunes a viernes de 8 a 17 h...").</summary>
    public string? ClosedMessage { get; set; }

    /// <summary>Lo que dice en el descanso; vacío = el de fuera de horario.</summary>
    public string? BreakMessage { get; set; }

    public BusinessHoursState StateAt(DateTimeOffset now)
    {
        if (!Enabled)
        {
            return BusinessHoursState.Open;
        }

        var local = TimeZoneInfo.ConvertTime(now, ResolveTimeZone());
        var time = TimeOnly.FromDateTime(local.DateTime);
        var day = Days.FirstOrDefault(d => d.Day == local.DayOfWeek);
        if (day is null || time < day.Opens || time >= day.Closes)
        {
            return BusinessHoursState.Closed;
        }

        return day.BreakStarts is { } breakStarts && day.BreakEnds is { } breakEnds && time >= breakStarts && time < breakEnds
            ? BusinessHoursState.OnBreak
            : BusinessHoursState.Open;
    }

    /// <summary>Mensaje para el estado; nulo si está abierto.</summary>
    public string? MessageFor(BusinessHoursState state) => state switch
    {
        BusinessHoursState.Closed => ClosedMessage,
        BusinessHoursState.OnBreak => string.IsNullOrWhiteSpace(BreakMessage) ? ClosedMessage : BreakMessage,
        _ => null,
    };

    /// <summary>Una zona inválida no debe tumbar la llamada: se usa UTC y el panel la valida al guardar.</summary>
    private TimeZoneInfo ResolveTimeZone() =>
        TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone) ? zone : TimeZoneInfo.Utc;
}
