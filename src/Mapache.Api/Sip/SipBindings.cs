using Mapache.Api.Realtime;
using Mapache.Infrastructure.Transfers;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Security;
using Mapache.Sip.Engine;
using Mapache.Sip.Media;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Sip;

/// <summary>
/// Lo que el motor SIP necesita de la base. Son singletons (como el motor) y abren un scope por operación
/// porque el DbContext es scoped.
/// </summary>
public sealed class SipAccountSource(IServiceScopeFactory scopes) : ISipAccountSource
{
    public async Task<IReadOnlyList<SipAccount>> GetEnabledAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MapacheDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var extensions = await db.Extensions.AsNoTracking().Where(e => e.Enabled).ToListAsync(ct);
        return extensions.Select(e => new SipAccount(e, protector.Unprotect(e.SipPasswordEncrypted))).ToList();
    }
}

public sealed class VoiceAgentSource(IServiceScopeFactory scopes) : IVoiceAgentSource
{
    public async Task<ElevenLabsOptions?> GetAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MapacheDbContext>();
        var settings = await db.VoiceSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        if (settings is not { ApiKeyEncrypted: { } key, AgentId: { Length: > 0 } agentId })
        {
            return null;
        }

        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        return new ElevenLabsOptions(protector.Unprotect(key), agentId);
    }
}

/// <summary>Guarda las llamadas y su transcripción, y avisa al panel en vivo.</summary>
public sealed class SipEventSink(
    IServiceScopeFactory scopes,
    IHubContext<EventsHub, IEventsClient> events,
    TimeProvider time,
    ILogger<SipEventSink> logger) : ISipEventSink
{
    public Task RegistrationChangedAsync(SipRegistrationStatus status) =>
        events.Clients.All.ExtensionStatusChanged(ExtensionStatusDto.From(status));

    public async Task<Guid> CallStartedAsync(Guid extensionId, string callerNumber, CallDirection direction)
    {
        var call = new Call
        {
            Id = Guid.CreateVersion7(),
            ExtensionId = extensionId,
            CallerNumber = callerNumber.Length > 64 ? callerNumber[..64] : callerNumber,
            Direction = direction,
            Status = CallStatus.Ringing,
            StartedAt = time.GetUtcNow(),
        };
        await WithDbAsync(db => db.Calls.Add(call));
        await PublishAsync(call.Id);
        return call.Id;
    }

    public async Task CallAnsweredAsync(Guid callId, string? conversationId)
    {
        await WithDbAsync(async db =>
        {
            if (await db.Calls.FindAsync(callId) is { } call)
            {
                call.Status = CallStatus.InProgress;
                call.AnsweredAt = time.GetUtcNow();
                call.ElevenLabsConversationId = conversationId;
            }
        });
        await PublishAsync(callId);
    }

    public async Task TranscriptAsync(Guid callId, TranscriptSpeaker speaker, string text)
    {
        try
        {
            await WithDbAsync(db => db.CallTranscripts.Add(new CallTranscript
            {
                CallId = callId,
                Speaker = speaker,
                Text = text,
                At = time.GetUtcNow(),
            }));
            await events.Clients.All.CallTranscript(callId, speaker, text);
        }
        catch (Exception ex)
        {
            // La transcripción no puede tumbar la llamada.
            logger.LogWarning(ex, "No se pudo guardar la transcripción de {CallId}", callId);
        }
    }

    public async Task CallEndedAsync(Guid callId, CallStatus status, string reason)
    {
        await WithDbAsync(async db =>
        {
            if (await db.Calls.FindAsync(callId) is { } call)
            {
                call.Status = status;
                call.EndReason = reason.Length > 256 ? reason[..256] : reason;
                call.EndedAt = time.GetUtcNow();
            }
        });
        await PublishAsync(callId);
    }

    private async Task PublishAsync(Guid callId)
    {
        await events.Clients.All.CallsChanged();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MapacheDbContext>();
            var live = await db.Calls.AsNoTracking()
                .Where(c => c.Id == callId)
                .Select(c => new LiveCallDto(
                    c.Id, c.ExtensionId, c.Extension!.Name, c.CallerNumber, c.Direction, c.Status, c.EndReason, c.StartedAt, c.AnsweredAt))
                .SingleOrDefaultAsync();
            if (live is not null)
            {
                await events.Clients.All.LiveCall(live);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo publicar la llamada {CallId}", callId);
        }
    }

    private Task WithDbAsync(Action<MapacheDbContext> action) => WithDbAsync(db =>
    {
        action(db);
        return Task.CompletedTask;
    });

    private async Task WithDbAsync(Func<MapacheDbContext, Task> action)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MapacheDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }
}

/// <summary>Estado de registro de una extensión para el panel.</summary>
public sealed record ExtensionStatusDto(Guid ExtensionId, string State, string Server, string? Error, int ActiveCalls, DateTimeOffset Since)
{
    public static ExtensionStatusDto From(SipRegistrationStatus s) =>
        new(s.ExtensionId, s.State.ToString(), s.Server, s.Error, s.ActiveCalls, s.Since);
}

/// <summary>Transferencias del Custom LLM (herramienta transfer_call) hacia el motor SIP.</summary>
public sealed class SipCallTransfer(ISipEngine engine) : ICallTransfer
{
    public Task<string> TransferAsync(Guid callId, string target, CancellationToken ct) => engine.TransferAsync(callId, target);
}
