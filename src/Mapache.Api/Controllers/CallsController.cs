using Mapache.Api.Audit;
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

/// <summary>Las API keys no salen de la API; solo se informa si están cargadas.</summary>
public sealed record VoiceSettingsDto(
    VoiceProvider Provider,
    bool HasApiKey,
    string? AgentId,
    bool HasOpenAiApiKey,
    string? OpenAiModel,
    string? OpenAiVoice,
    string? OpenAiTranscriptionModel,
    bool HasDeepgramApiKey,
    string? DeepgramLanguage,
    string? DeepgramListenModel,
    string? DeepgramSpeakModel,
    string? Greeting);

/// <summary>Cada API key vacía conserva la guardada.</summary>
public sealed record SaveVoiceSettingsRequest(
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? ApiKey,
    [StringLength(128, ErrorMessage = Messages.MaxLength)] string? AgentId,
    VoiceProvider Provider = VoiceProvider.ElevenLabs,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? OpenAiApiKey = null,
    [StringLength(64, ErrorMessage = Messages.MaxLength)] string? OpenAiModel = null,
    [StringLength(32, ErrorMessage = Messages.MaxLength)] string? OpenAiVoice = null,
    [StringLength(64, ErrorMessage = Messages.MaxLength)] string? OpenAiTranscriptionModel = null,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? DeepgramApiKey = null,
    [StringLength(16, ErrorMessage = Messages.MaxLength)] string? DeepgramLanguage = null,
    [StringLength(64, ErrorMessage = Messages.MaxLength)] string? DeepgramListenModel = null,
    [StringLength(64, ErrorMessage = Messages.MaxLength)] string? DeepgramSpeakModel = null,
    [StringLength(500, ErrorMessage = Messages.MaxLength)] string? Greeting = null);

[ApiController]
[Authorize]
public sealed class CallsController(MapacheDbContext db, ISipEngine engine, ISecretProtector protector, TimeProvider time, AuditLog audit) : ControllerBase
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
        return ToDto(settings);
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

        string? Protect(string? value, string? current) => string.IsNullOrWhiteSpace(value) ? current : protector.Protect(value.Trim());
        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        settings.Provider = request.Provider;
        settings.ApiKeyEncrypted = Protect(request.ApiKey, settings.ApiKeyEncrypted);
        settings.AgentId = Optional(request.AgentId);
        settings.OpenAiApiKeyEncrypted = Protect(request.OpenAiApiKey, settings.OpenAiApiKeyEncrypted);
        settings.OpenAiModel = Optional(request.OpenAiModel);
        settings.OpenAiVoice = Optional(request.OpenAiVoice);
        settings.OpenAiTranscriptionModel = Optional(request.OpenAiTranscriptionModel);
        settings.DeepgramApiKeyEncrypted = Protect(request.DeepgramApiKey, settings.DeepgramApiKeyEncrypted);
        settings.DeepgramLanguage = Optional(request.DeepgramLanguage);
        settings.DeepgramListenModel = Optional(request.DeepgramListenModel);
        settings.DeepgramSpeakModel = Optional(request.DeepgramSpeakModel);
        settings.Greeting = Optional(request.Greeting);
        settings.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.SettingsChanged, "Voz", details: $"Proveedor: {settings.Provider}");
        return ToDto(settings);
    }

    private static VoiceSettingsDto ToDto(VoiceSettings s) => new(
        s.Provider,
        s.ApiKeyEncrypted is not null,
        s.AgentId,
        s.OpenAiApiKeyEncrypted is not null,
        s.OpenAiModel,
        s.OpenAiVoice,
        s.OpenAiTranscriptionModel,
        s.DeepgramApiKeyEncrypted is not null,
        s.DeepgramLanguage,
        s.DeepgramListenModel,
        s.DeepgramSpeakModel,
        s.Greeting);

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
