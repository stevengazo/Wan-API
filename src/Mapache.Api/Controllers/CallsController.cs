using System.ComponentModel.DataAnnotations;
using Mapache.Api.Auth;
using Mapache.Api.Sip;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Security;
using Mapache.Sip.Engine;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record CallDto(
    Guid Id,
    Guid ExtensionId,
    string ExtensionName,
    string CallerNumber,
    CallDirection Direction,
    CallStatus Status,
    string? EndReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? AnsweredAt,
    DateTimeOffset? EndedAt,
    string? ConversationId,
    Guid? RecordingId);

public sealed record TranscriptLineDto(TranscriptSpeaker Speaker, string Text, DateTimeOffset At);

public sealed record CallDetailDto(CallDto Call, IReadOnlyList<TranscriptLineDto> Transcript);

/// <summary>La API key no sale de la API; solo se informa si está cargada.</summary>
public sealed record VoiceSettingsDto(bool HasApiKey, string? AgentId);

/// <summary><c>ApiKey</c> vacía conserva la guardada.</summary>
public sealed record SaveVoiceSettingsRequest(
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? ApiKey,
    [StringLength(128, ErrorMessage = Messages.MaxLength)] string? AgentId);

[ApiController]
[Authorize]
public sealed class CallsController(MapacheDbContext db, ISipEngine engine, ISecretProtector protector, TimeProvider time) : ControllerBase
{
    [HttpGet("api/calls")]
    public async Task<IReadOnlyList<CallDto>> List([FromQuery] int limit = 100, CancellationToken ct = default) =>
        await Project(db.Calls.OrderByDescending(c => c.StartedAt).Take(Math.Clamp(limit, 1, 500))).ToListAsync(ct);

    [HttpGet("api/calls/{id:guid}")]
    public async Task<ActionResult<CallDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var call = await Project(db.Calls.Where(c => c.Id == id)).SingleOrDefaultAsync(ct);
        if (call is null)
        {
            return NotFound();
        }

        var transcript = await db.CallTranscripts.AsNoTracking()
            .Where(t => t.CallId == id)
            .OrderBy(t => t.At)
            .Select(t => new TranscriptLineDto(t.Speaker, t.Text, t.At))
            .ToListAsync(ct);
        return new CallDetailDto(call, transcript);
    }

    /// <summary>Estado de registro de cada extensión según el motor SIP.</summary>
    [HttpGet("api/extensions/status")]
    public IReadOnlyList<ExtensionStatusDto> Statuses() => engine.GetStatuses().Select(ExtensionStatusDto.From).ToList();

    [HttpGet("api/settings/voice")]
    public async Task<VoiceSettingsDto> GetVoice(CancellationToken ct)
    {
        var settings = await db.VoiceSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new VoiceSettings();
        return new VoiceSettingsDto(settings.ApiKeyEncrypted is not null, settings.AgentId);
    }

    [HttpPut("api/settings/voice")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<VoiceSettingsDto> SaveVoice(SaveVoiceSettingsRequest request, CancellationToken ct)
    {
        var settings = await db.VoiceSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new VoiceSettings();
            db.VoiceSettings.Add(settings);
        }

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            settings.ApiKeyEncrypted = protector.Protect(request.ApiKey.Trim());
        }

        settings.AgentId = string.IsNullOrWhiteSpace(request.AgentId) ? null : request.AgentId.Trim();
        settings.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new VoiceSettingsDto(settings.ApiKeyEncrypted is not null, settings.AgentId);
    }

    private IQueryable<CallDto> Project(IQueryable<Call> query) =>
        query.AsNoTracking().Select(c => new CallDto(
            c.Id,
            c.ExtensionId,
            c.Extension!.Name,
            c.CallerNumber,
            c.Direction,
            c.Status,
            c.EndReason,
            c.StartedAt,
            c.AnsweredAt,
            c.EndedAt,
            c.ElevenLabsConversationId,
            db.Recordings.Where(r => r.ConversationId == c.ElevenLabsConversationId).Select(r => (Guid?)r.Id).FirstOrDefault()));
}
