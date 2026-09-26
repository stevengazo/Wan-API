using Wan.Api.Audit;
using System.ComponentModel.DataAnnotations;
using Wan.Api.Auth;
using Wan.Api.Realtime;
using Wan.Domain.Entities;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Recordings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Wan.Api.Controllers;

public sealed record RecordingDto(
    Guid Id,
    string ConversationId,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    StorageProvider Storage,
    string ContentType,
    Guid? CallId,
    double? DurationSeconds);

/// <summary>Días de retención nulos = sin borrado automático.</summary>
public sealed record CallSettingsDto(bool RecordCalls, string? RecordingNotice, int? RecordingRetentionDays, int? TranscriptRetentionDays);

public sealed record SaveCallSettingsRequest(
    bool RecordCalls,
    [StringLength(1000, ErrorMessage = Messages.MaxLength)] string? RecordingNotice,
    [Range(1, 3650, ErrorMessage = Messages.Range)] int? RecordingRetentionDays = null,
    [Range(1, 3650, ErrorMessage = Messages.Range)] int? TranscriptRetentionDays = null);

[ApiController]
public sealed class RecordingsController(
    WanDbContext db,
    RecordingService recordings,
    TimeProvider time,
    IHubContext<EventsHub, IEventsClient> events,
    AuditLog audit) : ControllerBase
{
    [HttpGet("api/recordings")]
    [Authorize]
    public async Task<IReadOnlyList<RecordingDto>> List(CancellationToken ct) =>
        await db.Recordings.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(500)
            .Select(r => new RecordingDto(r.Id, r.ConversationId, r.SizeBytes, r.CreatedAt, r.Storage, r.ContentType, r.CallId, r.DurationSeconds))
            .ToListAsync(ct);

    /// <summary>
    /// El audio con soporte de rangos (para adelantar) si está en disco; en S3 o Azure, una redirección a un enlace
    /// firmado de pocos minutos.
    /// </summary>
    [HttpGet("api/recordings/{id:guid}/audio")]
    [Authorize]
    public async Task<IActionResult> Audio(Guid id, CancellationToken ct)
    {
        var recording = await db.Recordings.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (recording is null || await recordings.OpenAsync(recording, ct) is not { } location)
        {
            return NotFound();
        }

        await audit.RecordAsync(AuditActions.RecordingPlayed, $"Grabación del {recording.CreatedAt:dd/MM/yyyy HH:mm}", recording.Id.ToString());
        var extension = recording.ContentType == "audio/wav" ? "wav" : "mp3";
        var fileName = $"grabacion-{recording.CreatedAt:yyyyMMdd-HHmm}.{extension}";
        return location switch
        {
            { Content: { } content } => File(content, location.ContentType, fileName, enableRangeProcessing: true),
            { Url: { } url } => Redirect(url.ToString()),
            _ => PhysicalFile(location.LocalPath!, location.ContentType, fileName, enableRangeProcessing: true),
        };
    }

    /// <summary>
    /// Enlace firmado para reproducir desde la nube sin token (el &lt;audio&gt; no manda headers); nulo si la
    /// grabación está en disco y hay que pedirla a <c>/audio</c>.
    /// </summary>
    [HttpGet("api/recordings/{id:guid}/location")]
    [Authorize]
    public async Task<IActionResult> Location(Guid id, CancellationToken ct)
    {
        var recording = await db.Recordings.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (recording is null || await recordings.OpenAsync(recording, ct) is not { } location)
        {
            return NotFound();
        }

        if (location.Url is not null)
        {
            // En disco se registra al pedir /audio; en la nube el audio no pasa por la API.
            await audit.RecordAsync(AuditActions.RecordingPlayed, $"Grabación del {recording.CreatedAt:dd/MM/yyyy HH:mm}", recording.Id.ToString());
        }

        return Ok(new { url = location.Url?.ToString() });
    }

    [HttpDelete("api/recordings/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var recording = await db.Recordings.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (recording is null)
        {
            return NotFound();
        }

        await recordings.DeleteAsync(recording, ct);
        db.Recordings.Remove(recording);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.RecordingDeleted, $"Grabación del {recording.CreatedAt:dd/MM/yyyy HH:mm}", recording.Id.ToString());
        await events.Clients.All.RecordingsChanged();
        return NoContent();
    }

    /// <summary>Aplica la retención ahora, sin esperar al trabajo diario.</summary>
    [HttpPost("api/settings/calls/retention/run")]
    [Authorize(Roles = Roles.Admin)]
    public Task<RetentionResult> RunRetention([FromServices] RetentionJob job, CancellationToken ct) => job.RunAsync(ct);

    [HttpGet("api/settings/calls")]
    [Authorize]
    public async Task<CallSettingsDto> GetSettings(CancellationToken ct)
    {
        var settings = await db.CallSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new CallSettings();
        return new CallSettingsDto(settings.RecordCalls, settings.RecordingNotice, settings.RecordingRetentionDays, settings.TranscriptRetentionDays);
    }

    [HttpPut("api/settings/calls")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<CallSettingsDto> SaveSettings(SaveCallSettingsRequest request, CancellationToken ct)
    {
        var settings = await db.CallSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new CallSettings();
            db.CallSettings.Add(settings);
        }

        settings.RecordCalls = request.RecordCalls;
        settings.RecordingNotice = string.IsNullOrWhiteSpace(request.RecordingNotice) ? null : request.RecordingNotice.Trim();
        settings.RecordingRetentionDays = request.RecordingRetentionDays;
        settings.TranscriptRetentionDays = request.TranscriptRetentionDays;
        settings.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            AuditActions.SettingsChanged,
            "Llamadas",
            details: $"Grabación {(settings.RecordCalls ? "activada" : "desactivada")}; retención grabaciones {settings.RecordingRetentionDays?.ToString() ?? "sin límite"} días, transcripciones {settings.TranscriptRetentionDays?.ToString() ?? "sin límite"} días");
        return new CallSettingsDto(settings.RecordCalls, settings.RecordingNotice, settings.RecordingRetentionDays, settings.TranscriptRetentionDays);
    }
}
