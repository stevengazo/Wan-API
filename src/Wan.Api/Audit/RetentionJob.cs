using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Recordings;
using Microsoft.EntityFrameworkCore;

namespace Wan.Api.Audit;

public sealed record RetentionResult(int Recordings, int TranscriptLines);

/// <summary>
/// Borrado automático según la retención de Configuración → Llamadas: grabaciones (del almacenamiento y de la base)
/// y transcripciones. Corre una vez por día con Hangfire y deja constancia en la auditoría.
/// </summary>
public sealed class RetentionJob(WanDbContext db, RecordingService recordings, AuditLog audit, TimeProvider time, ILogger<RetentionJob> logger)
{
    public const string JobId = "retencion";

    /// <summary>Devuelve cuántas grabaciones y líneas de transcripción se borraron.</summary>
    public async Task<RetentionResult> RunAsync(CancellationToken ct)
    {
        var result = new RetentionResult(0, 0);
        var settings = await db.CallSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            return result;
        }

        var now = time.GetUtcNow();
        if (settings.RecordingRetentionDays is { } recordingDays)
        {
            var limit = now.AddDays(-recordingDays);
            var expired = await db.Recordings.Where(r => r.CreatedAt < limit).ToListAsync(ct);
            var deleted = 0;
            foreach (var recording in expired)
            {
                try
                {
                    await recordings.DeleteAsync(recording, ct);
                    db.Recordings.Remove(recording);
                    deleted++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Una grabación que no se pudo borrar del almacenamiento se reintenta mañana.
                    logger.LogWarning(ex, "No se pudo borrar la grabación {RecordingId}", recording.Id);
                }
            }

            await db.SaveChangesAsync(ct);
            result = result with { Recordings = deleted };
            if (deleted > 0)
            {
                await audit.RecordAsync(AuditActions.RetentionPurged, $"{deleted} grabaciones", details: $"Anteriores al {limit:dd/MM/yyyy} ({recordingDays} días)");
            }
        }

        if (settings.TranscriptRetentionDays is { } transcriptDays)
        {
            var limit = now.AddDays(-transcriptDays);
            var deleted = await db.CallTranscripts.Where(t => t.At < limit).ExecuteDeleteAsync(ct);
            result = result with { TranscriptLines = deleted };
            if (deleted > 0)
            {
                await audit.RecordAsync(AuditActions.RetentionPurged, $"{deleted} líneas de transcripción", details: $"Anteriores al {limit:dd/MM/yyyy} ({transcriptDays} días)");
            }
        }

        return result;
    }
}
