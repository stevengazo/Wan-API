using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record AnalyticsTotalsDto(
    int Calls,
    int Inbound,
    int Outbound,
    int Answered,
    int Missed,
    int Failed,
    int Transferred,
    int TakenByOperator,
    double AnswerRate,
    double AverageDurationSeconds,
    double TalkMinutes,
    int Messages,
    int FormSubmissions);

public sealed record AnalyticsDayDto(DateOnly Date, int Inbound, int Outbound, int Answered, int Missed);

/// <summary>Llamadas por día de la semana (0 = lunes) y hora local.</summary>
public sealed record AnalyticsHeatCellDto(int Day, int Hour, int Calls);

public sealed record AnalyticsExtensionDto(Guid ExtensionId, string Name, int Calls, int Answered, double AverageDurationSeconds);

public sealed record AnalyticsReasonDto(string Reason, int Calls);

public sealed record AnalyticsDto(
    DateTimeOffset From,
    DateTimeOffset To,
    AnalyticsTotalsDto Totals,
    IReadOnlyList<AnalyticsDayDto> Days,
    IReadOnlyList<AnalyticsHeatCellDto> Heatmap,
    IReadOnlyList<AnalyticsExtensionDto> Extensions,
    IReadOnlyList<AnalyticsReasonDto> EndReasons,
    IReadOnlyDictionary<string, int> CampaignOutcomes);

/// <summary>Métricas de llamadas, recados, formularios y campañas en un período, en la zona horaria de quien mira.</summary>
[ApiController]
[Authorize]
public sealed class AnalyticsController(MapacheDbContext db, TimeProvider time) : ControllerBase
{
    private const int MaxDays = 366;

    [HttpGet("api/analytics")]
    public async Task<ActionResult<AnalyticsDto>> Get(
        [FromQuery] int days = 30, [FromQuery] Guid? extensionId = null, [FromQuery] string? timeZone = null, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, MaxDays);
        var zone = timeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var found) ? found : TimeZoneInfo.Utc;
        var to = time.GetUtcNow();
        // Desde el inicio del primer día local: los días del gráfico quedan completos.
        var localStart = TimeZoneInfo.ConvertTime(to, zone).Date.AddDays(1 - days);
        var from = new DateTimeOffset(localStart, zone.GetUtcOffset(localStart)).ToUniversalTime();

        var query = db.Calls.AsNoTracking().Where(c => c.StartedAt >= from);
        if (extensionId is { } id)
        {
            query = query.Where(c => c.ExtensionId == id);
        }

        var calls = await query
            .Select(c => new { c.ExtensionId, ExtensionName = c.Extension!.Name, c.Direction, c.Status, c.EndReason, c.StartedAt, c.AnsweredAt, c.EndedAt })
            .ToListAsync(ct);

        static bool IsAnswered(CallStatus status, DateTimeOffset? answeredAt) =>
            answeredAt is not null && status is CallStatus.Completed or CallStatus.Transferred or CallStatus.InProgress;
        static double? Duration(DateTimeOffset? answered, DateTimeOffset? ended) =>
            answered is { } a && ended is { } e && e > a ? (e - a).TotalSeconds : null;

        var answered = calls.Where(c => IsAnswered(c.Status, c.AnsweredAt)).ToList();
        var durations = answered.Select(c => Duration(c.AnsweredAt, c.EndedAt)).OfType<double>().ToList();

        var totals = new AnalyticsTotalsDto(
            calls.Count,
            calls.Count(c => c.Direction == CallDirection.Inbound),
            calls.Count(c => c.Direction == CallDirection.Outbound),
            answered.Count,
            calls.Count(c => c.Status == CallStatus.Missed),
            calls.Count(c => c.Status == CallStatus.Failed),
            calls.Count(c => c.Status == CallStatus.Transferred),
            calls.Count(c => c.EndReason != null && c.EndReason.StartsWith("la tomó un operador")),
            calls.Count == 0 ? 0 : Math.Round((double)answered.Count / calls.Count, 3),
            durations.Count == 0 ? 0 : Math.Round(durations.Average(), 1),
            Math.Round(durations.Sum() / 60, 1),
            await db.CallMessages.CountAsync(m => m.CreatedAt >= from, ct),
            await db.FormSubmissions.CountAsync(s => s.CreatedAt >= from, ct));

        DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).DateTime;

        var byDay = calls.GroupBy(c => DateOnly.FromDateTime(Local(c.StartedAt))).ToDictionary(g => g.Key);
        var dayList = Enumerable.Range(0, days)
            .Select(i => DateOnly.FromDateTime(localStart.AddDays(i)))
            .Select(d => byDay.TryGetValue(d, out var g)
                ? new AnalyticsDayDto(
                    d,
                    g.Count(c => c.Direction == CallDirection.Inbound),
                    g.Count(c => c.Direction == CallDirection.Outbound),
                    g.Count(c => IsAnswered(c.Status, c.AnsweredAt)),
                    g.Count(c => c.Status == CallStatus.Missed))
                : new AnalyticsDayDto(d, 0, 0, 0, 0))
            .ToList();

        var heatmap = calls
            .GroupBy(c =>
            {
                var local = Local(c.StartedAt);
                return (Day: ((int)local.DayOfWeek + 6) % 7, local.Hour);
            })
            .Select(g => new AnalyticsHeatCellDto(g.Key.Day, g.Key.Hour, g.Count()))
            .ToList();

        var extensions = calls
            .GroupBy(c => (c.ExtensionId, c.ExtensionName))
            .Select(g =>
            {
                var talked = g.Select(c => Duration(c.AnsweredAt, c.EndedAt)).OfType<double>().ToList();
                return new AnalyticsExtensionDto(
                    g.Key.ExtensionId, g.Key.ExtensionName, g.Count(), g.Count(c => IsAnswered(c.Status, c.AnsweredAt)),
                    talked.Count == 0 ? 0 : Math.Round(talked.Average(), 1));
            })
            .OrderByDescending(e => e.Calls)
            .ToList();

        var reasons = calls
            .Where(c => !string.IsNullOrWhiteSpace(c.EndReason))
            .GroupBy(c => Simplify(c.EndReason!))
            .Select(g => new AnalyticsReasonDto(g.Key, g.Count()))
            .OrderByDescending(r => r.Calls)
            .Take(8)
            .ToList();

        var outcomes = await db.CampaignContacts.AsNoTracking()
            .Where(c => c.Outcome != null && c.UpdatedAt >= from)
            .GroupBy(c => c.Outcome!)
            .Select(g => new { Outcome = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Outcome, g => g.Count, ct);

        return new AnalyticsDto(from, to, totals, dayList, heatmap, extensions, reasons, outcomes);
    }

    /// <summary>Agrupa motivos con datos variables ("transferida a sip:101@…" → "transferida").</summary>
    private static string Simplify(string reason)
    {
        var text = reason.Trim();
        foreach (var prefix in new[] { "transferida", "la tomó un operador", "no contestó", "ElevenLabs", "OpenAI", "Deepgram", "voz:" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return prefix == "voz:" ? "terminó la conversación" : prefix;
            }
        }

        return text.Length > 60 ? text[..60] + "…" : text;
    }
}
