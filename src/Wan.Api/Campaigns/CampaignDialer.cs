using Mapache.Api.Realtime;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Campaigns;
using Mapache.Infrastructure.Persistence;
using Mapache.Sip.Engine;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Campaigns;

/// <summary>
/// Marcador de campañas: cada pocos segundos toma los contactos pendientes de las campañas en curso y lanza
/// llamadas del bot sin pasar el límite de simultáneas de cada campaña ni el horario de su extensión.
/// </summary>
public sealed class CampaignDialer(
    IServiceScopeFactory scopes,
    ISipEngine engine,
    TimeProvider time,
    IHubContext<EventsHub, IEventsClient> events,
    ILogger<CampaignDialer> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await DialAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error en el marcador de campañas");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Si la API se reinició a mitad de una llamada, ese contacto vuelve a la cola.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MapacheDbContext>();
        var stuck = await db.CampaignContacts.Where(c => c.Status == CampaignContactStatus.Calling)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CampaignContactStatus.Pending), ct);
        if (stuck > 0)
        {
            logger.LogWarning("{Count} contactos de campaña quedaron a mitad de llamada y vuelven a la cola", stuck);
        }
    }

    private async Task DialAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MapacheDbContext>();
        var now = time.GetUtcNow();
        var campaigns = await db.Campaigns.Include(c => c.Extension).Where(c => c.Status == CampaignStatus.Running).ToListAsync(ct);

        foreach (var campaign in campaigns)
        {
            var calling = await db.CampaignContacts.CountAsync(c => c.CampaignId == campaign.Id && c.Status == CampaignContactStatus.Calling, ct);
            var hasPending = await db.CampaignContacts.AnyAsync(c => c.CampaignId == campaign.Id && c.Status == CampaignContactStatus.Pending, ct);
            if (!hasPending && calling == 0)
            {
                campaign.Status = CampaignStatus.Completed;
                campaign.CompletedAt = now;
                await db.SaveChangesAsync(ct);
                await events.Clients.All.CampaignsChanged();
                logger.LogInformation("Campaña {Name} completa", campaign.Name);
                continue;
            }

            if (campaign.RespectBusinessHours && campaign.Extension?.BusinessHours.StateAt(now) is not (null or BusinessHoursState.Open))
            {
                continue;
            }

            var slots = campaign.MaxConcurrentCalls - calling;
            if (slots <= 0)
            {
                continue;
            }

            var next = await db.CampaignContacts
                .Where(c => c.CampaignId == campaign.Id && c.Status == CampaignContactStatus.Pending && (c.NextAttemptAt == null || c.NextAttemptAt <= now))
                .OrderBy(c => c.NextAttemptAt).ThenBy(c => c.CreatedAt)
                .Take(slots)
                .ToListAsync(ct);

            foreach (var contact in next)
            {
                contact.Status = CampaignContactStatus.Calling;
                contact.Attempts++;
                contact.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct);
            foreach (var contact in next)
            {
                var opening = campaign.OpeningLine is { Length: > 0 } line
                    ? CampaignPrompt.Render(line, CampaignPrompt.Variables(contact))
                    : null;
                var request = new BotCallRequest(campaign.ExtensionId, contact.PhoneNumber, campaign.Id, contact.Id, opening);
                // Cada llamada corre por su cuenta; el marcador sigue con las demás campañas.
                _ = Task.Run(() => CallAsync(campaign, contact.Id, request, ct), ct);
            }

            if (next.Count > 0)
            {
                await events.Clients.All.CampaignsChanged();
            }
        }
    }

    private async Task CallAsync(Campaign campaign, Guid contactId, BotCallRequest request, CancellationToken ct)
    {
        BotCallResult result;
        try
        {
            result = await engine.DialBotAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Extensión caída o sin voz: no es culpa del contacto, se reintenta como si no hubiera contestado.
            logger.LogWarning(ex, "No se pudo llamar al contacto {ContactId}", contactId);
            result = new BotCallResult(null, false, false, ex.Message);
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MapacheDbContext>();
        var contact = await db.CampaignContacts.SingleOrDefaultAsync(c => c.Id == contactId, CancellationToken.None);
        if (contact is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        contact.LastCallId = result.CallId ?? contact.LastCallId;
        contact.LastResult = result.Reason.Length > 256 ? result.Reason[..256] : result.Reason;
        contact.UpdatedAt = now;
        if (result.Answered)
        {
            contact.Status = CampaignContactStatus.Completed;
        }
        else if (contact.Attempts < campaign.MaxAttempts)
        {
            contact.Status = CampaignContactStatus.Pending;
            contact.NextAttemptAt = now.AddMinutes(campaign.RetryMinutes);
        }
        else
        {
            contact.Status = result.Busy ? CampaignContactStatus.Busy
                : result.CallId is null ? CampaignContactStatus.Failed
                : CampaignContactStatus.NoAnswer;
        }

        await db.SaveChangesAsync(CancellationToken.None);
        await events.Clients.All.CampaignsChanged();
    }
}
