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

public sealed record RecordingDto(Guid Id, string ConversationId, long SizeBytes, DateTimeOffset CreatedAt);

public sealed record CallSettingsDto(bool RecordCalls, string? RecordingNotice);

public sealed record SaveCallSettingsRequest(
    bool RecordCalls,
    [StringLength(1000, ErrorMessage = Messages.MaxLength)] string? RecordingNotice);

[ApiController]
public sealed class RecordingsController(
    MapacheDbContext db,
    RecordingStore store,
    TimeProvider time,
    IHubContext<EventsHub, IEventsClient> events) : ControllerBase
{
    [HttpGet("api/recordings")]
    [Authorize]
    public async Task<IReadOnlyList<RecordingDto>> List(CancellationToken ct) =>
        await db.Recordings.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(500)
            .Select(r => new RecordingDto(r.Id, r.ConversationId, r.SizeBytes, r.CreatedAt))
            .ToListAsync(ct);

    /// <summary>El MP3 con soporte de rangos, para que el reproductor pueda adelantar.</summary>
    [HttpGet("api/recordings/{id:guid}/audio")]
    [Authorize]
    public async Task<IActionResult> Audio(Guid id, CancellationToken ct)
    {
        var recording = await db.Recordings.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        return recording is not null && store.PathFor(recording.FileName) is { } path
            ? PhysicalFile(path, "audio/mpeg", $"grabacion-{recording.CreatedAt:yyyyMMdd-HHmm}.mp3", enableRangeProcessing: true)
            : NotFound();
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

        db.Recordings.Remove(recording);
        await db.SaveChangesAsync(ct);
        store.Delete(recording.FileName);
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
