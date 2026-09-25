using Microsoft.Extensions.Options;
using Mapache.Infrastructure.Tools;
using Mapache.Infrastructure.Llm;
using Mapache.Api.Webhooks;
using System.Text.Json.Nodes;
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

public sealed class VoiceAgentSource(IServiceScopeFactory scopes, IOptions<AgentWebhookOptions> agentWebhooks, ILogger<VoiceAgentSource> logger)
    : IVoiceAgentSource
{
    public async Task<VoiceProviderOptions?> GetAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MapacheDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var settings = await db.VoiceSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            return null;
        }

        string? Unprotect(string? value) => value is null ? null : protector.Unprotect(value);

        switch (settings.Provider)
        {
            case VoiceProvider.OpenAiRealtime:
                // Sin key propia se usa la de OpenAI de Configuración → IA: suele ser la misma cuenta.
                var openAiKey = Unprotect(settings.OpenAiApiKeyEncrypted) ?? Unprotect(await db.LlmProviderSettings.AsNoTracking()
                    .Where(p => p.Provider == LlmProvider.OpenAi).Select(p => p.ApiKeyEncrypted).SingleOrDefaultAsync(ct));
                return openAiKey is null
                    ? null
                    : new OpenAiRealtimeOptions(
                        openAiKey,
                        settings.OpenAiModel ?? VoiceDefaults.OpenAiModel,
                        settings.OpenAiVoice ?? VoiceDefaults.OpenAiVoice,
                        settings.OpenAiTranscriptionModel ?? VoiceDefaults.OpenAiTranscriptionModel,
                        settings.Greeting);

            case VoiceProvider.Deepgram:
                var publicUrl = await scope.ServiceProvider.GetRequiredService<PublicUrlResolver>().ResolveAsync(ct);
                if (settings.DeepgramApiKeyEncrypted is null || publicUrl is null || agentWebhooks.Value.ToolToken.Length == 0)
                {
                    // Deepgram llama al Custom LLM desde su nube: sin URL pública no hay a dónde.
                    logger.LogWarning("Deepgram necesita API key, URL pública y AgentWebhooks:ToolToken");
                    return null;
                }

                return new DeepgramAgentOptions(
                    protector.Unprotect(settings.DeepgramApiKeyEncrypted),
                    settings.DeepgramLanguage ?? VoiceDefaults.DeepgramLanguage,
                    settings.DeepgramListenModel ?? VoiceDefaults.DeepgramListenModel,
                    settings.DeepgramSpeakModel ?? VoiceDefaults.DeepgramSpeakModel,
                    settings.Greeting,
                    $"{publicUrl}/api/llm",
                    agentWebhooks.Value.ToolToken);

            default:
                return settings is { ApiKeyEncrypted: { } key, AgentId: { Length: > 0 } agentId }
                    ? new ElevenLabsOptions(protector.Unprotect(key), agentId)
                    : null;
        }
    }
}

/// <summary>
/// Instrucciones y herramientas para OpenAI Realtime, con el mismo <see cref="BotSessionBuilder"/> que el Custom
/// LLM. Cada herramienta se ejecuta en un scope propio: la llamada dura más que cualquier scope de request.
/// </summary>
public sealed class BotBrain(IServiceScopeFactory scopes) : IBotBrain
{
    public async Task<BotBrainSession> StartAsync(VoiceCallContext voiceCall, CancellationToken ct)
    {
        var call = new BotCallContext(voiceCall.ExtensionId, voiceCall.CallId, voiceCall.CampaignId, voiceCall.ContactId);
        BotSession session;
        using (var scope = scopes.CreateScope())
        {
            session = await scope.ServiceProvider.GetRequiredService<BotSessionBuilder>().BuildAsync(call, [], null, ct);
        }

        var tools = session.Tools.Values.Select(t => new VoiceToolSpec(t.Name, t.Description, t.Parameters)).ToList();
        return new BotBrainSession(
            session.Instructions("Eres la recepción telefónica de la empresa. Sé breve, cordial y habla en español."),
            tools,
            async (name, arguments, token) =>
            {
                using var scope = scopes.CreateScope();
                var available = await scope.ServiceProvider.GetRequiredService<BotToolRegistry>().LoadAsync(call, token);
                if (!available.TryGetValue(name, out var tool))
                {
                    return "Esa herramienta ya no está disponible.";
                }

                var args = string.IsNullOrWhiteSpace(arguments) ? [] : JsonNode.Parse(arguments) as JsonObject ?? [];
                return await tool.ExecuteAsync(args, token);
            });
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
