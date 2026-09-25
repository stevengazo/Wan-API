using System.ComponentModel.DataAnnotations;
using Mapache.Api.Auth;
using Mapache.Api.Realtime;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Recordings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record RecordingDto(
    Guid Id,
    string ConversationId,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    StorageProvider Storage,
    string ContentType,
    Guid? CallId,
    double? DurationSeconds);

public sealed record CallSettingsDto(bool RecordCalls, string? RecordingNotice);

public sealed record SaveCallSettingsRequest(
    bool RecordCalls,
    [StringLength(1000, ErrorMessage = Messages.MaxLength)] string? RecordingNotice);

[ApiController]
public sealed class RecordingsController(
    MapacheDbContext db,
    RecordingService recordings,
    TimeProvider time,
    IHubContext<EventsHub, IEventsClient> events) : ControllerBase
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

        var extension = recording.ContentType == "audio/wav" ? "wav" : "mp3";
        return location.Url is { } url
            ? Redirect(url.ToString())
            : PhysicalFile(location.LocalPath!, location.ContentType, $"grabacion-{recording.CreatedAt:yyyyMMdd-HHmm}.{extension}", enableRangeProcessing: true);
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
        await events.Clients.All.RecordingsChanged();
        return NoContent();
    }

    [HttpGet("api/settings/calls")]
    [Authorize]
    public async Task<CallSettingsDto> GetSettings(CancellationToken ct)
    {
        var settings = await db.CallSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new CallSettings();
        return new CallSettingsDto(settings.RecordCalls, settings.RecordingNotice);
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
        settings.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new CallSettingsDto(settings.RecordCalls, settings.RecordingNotice);
    }
}
