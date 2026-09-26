using Wan.Api.Audit;
using System.ComponentModel.DataAnnotations;
using Wan.Api.Auth;
using Wan.Api.Realtime;
using Wan.Domain.Entities;
using Wan.Infrastructure.Campaigns;
using Wan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Wan.Api.Controllers;

/// <summary>Cuántos contactos hay en cada estado y cuántos con cada resultado del bot.</summary>
public sealed record CampaignProgressDto(
    int Total,
    int Pending,
    int Calling,
    int Completed,
    int NoAnswer,
    int Busy,
    int Failed,
    IReadOnlyDictionary<string, int> Outcomes);

public sealed record CampaignDto(
    Guid Id,
    string Name,
    Guid ExtensionId,
    string ExtensionName,
    string Instructions,
    string? OpeningLine,
    IReadOnlyList<string> Outcomes,
    CampaignStatus Status,
    int MaxConcurrentCalls,
    int MaxAttempts,
    int RetryMinutes,
    bool RespectBusinessHours,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    CampaignProgressDto Progress);

public sealed record SaveCampaignRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Name,
    Guid ExtensionId,
    [Required(ErrorMessage = Messages.Required), StringLength(8000, ErrorMessage = Messages.MaxLength)] string Instructions,
    [StringLength(500, ErrorMessage = Messages.MaxLength)] string? OpeningLine,
    IReadOnlyList<string>? Outcomes,
    [Range(1, 20, ErrorMessage = Messages.Range)] int MaxConcurrentCalls = 1,
    [Range(1, 10, ErrorMessage = Messages.Range)] int MaxAttempts = 2,
    [Range(1, 10080, ErrorMessage = Messages.Range)] int RetryMinutes = 60,
    bool RespectBusinessHours = true);

public sealed record CampaignContactDto(
    Guid Id,
    string PhoneNumber,
    string? Name,
    IReadOnlyDictionary<string, string> Variables,
    CampaignContactStatus Status,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    Guid? LastCallId,
    string? LastResult,
    string? Outcome,
    string? Notes,
    DateTimeOffset? UpdatedAt);

public sealed record ImportContactsRequest([Required(ErrorMessage = Messages.Required)] string Csv);

public sealed record ImportContactsResult(int Imported, int Skipped, IReadOnlyList<string> Problems);

[ApiController]
[Authorize]
public sealed class CampaignsController(WanDbContext db, TimeProvider time, IHubContext<EventsHub, IEventsClient> events, AuditLog audit) : ControllerBase
{
    private static readonly string[] DefaultOutcomes = ["Interesado", "No interesado", "Volver a llamar"];

    [HttpGet("api/campaigns")]
    public async Task<IReadOnlyList<CampaignDto>> List(CancellationToken ct)
    {
        var campaigns = await db.Campaigns.AsNoTracking().Include(c => c.Extension).OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        var progress = await ProgressAsync(campaigns.Select(c => c.Id).ToList(), ct);
        return campaigns.Select(c => ToDto(c, progress.GetValueOrDefault(c.Id))).ToList();
    }

    [HttpGet("api/campaigns/{id:guid}")]
    public async Task<ActionResult<CampaignDto>> Get(Guid id, CancellationToken ct)
    {
        var campaign = await db.Campaigns.AsNoTracking().Include(c => c.Extension).SingleOrDefaultAsync(c => c.Id == id, ct);
        return campaign is null ? NotFound() : ToDto(campaign, (await ProgressAsync([id], ct)).GetValueOrDefault(id));
    }

    [HttpPost("api/campaigns")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<CampaignDto>> Create(SaveCampaignRequest request, CancellationToken ct)
    {
        if (await ValidateAsync(request, ct) is { } problem)
        {
            return problem;
        }

        var campaign = new Campaign { Id = Guid.CreateVersion7(), Name = "", Instructions = "", CreatedAt = time.GetUtcNow() };
        Apply(campaign, request);
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        await events.Clients.All.CampaignsChanged();
        return (await Get(campaign.Id, ct)).Value!;
    }

    [HttpPut("api/campaigns/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<CampaignDto>> Update(Guid id, SaveCampaignRequest request, CancellationToken ct)
    {
        var campaign = await db.Campaigns.FindAsync([id], ct);
        if (campaign is null)
        {
            return NotFound();
        }

        if (await ValidateAsync(request, ct) is { } problem)
        {
            return problem;
        }

        Apply(campaign, request);
        await db.SaveChangesAsync(ct);
        await events.Clients.All.CampaignsChanged();
        return await Get(id, ct);
    }

    [HttpDelete("api/campaigns/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var campaign = await db.Campaigns.FindAsync([id], ct);
        if (campaign is null)
        {
            return NotFound();
        }

        if (campaign.Status == CampaignStatus.Running)
        {
            return Conflict(new ProblemDetails { Title = "Pausa la campaña antes de eliminarla" });
        }

        db.Campaigns.Remove(campaign);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.CampaignDeleted, campaign.Name, id.ToString());
        await events.Clients.All.CampaignsChanged();
        return NoContent();
    }

    /// <summary>Empieza o reanuda: el marcador toma los pendientes en unos segundos.</summary>
    [HttpPost("api/campaigns/{id:guid}/start")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<CampaignDto>> Start(Guid id, CancellationToken ct)
    {
        var campaign = await db.Campaigns.FindAsync([id], ct);
        if (campaign is null)
        {
            return NotFound();
        }

        if (!await db.CampaignContacts.AnyAsync(c => c.CampaignId == id && c.Status == CampaignContactStatus.Pending, ct))
        {
            return Conflict(new ProblemDetails { Title = "La campaña no tiene contactos pendientes" });
        }

        campaign.Status = CampaignStatus.Running;
        campaign.StartedAt ??= time.GetUtcNow();
        campaign.CompletedAt = null;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.CampaignStarted, campaign.Name, id.ToString());
        await events.Clients.All.CampaignsChanged();
        return await Get(id, ct);
    }

    /// <summary>Deja de marcar; las llamadas en curso terminan normalmente.</summary>
    [HttpPost("api/campaigns/{id:guid}/pause")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<CampaignDto>> Pause(Guid id, CancellationToken ct)
    {
        if (await db.Campaigns.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CampaignStatus.Paused), ct) == 0)
        {
            return NotFound();
        }

        await audit.RecordAsync(AuditActions.CampaignPaused, await db.Campaigns.Where(c => c.Id == id).Select(c => c.Name).SingleAsync(ct), id.ToString());

        await events.Clients.All.CampaignsChanged();
        return await Get(id, ct);
    }

    [HttpGet("api/campaigns/{id:guid}/contacts")]
    public async Task<IReadOnlyList<CampaignContactDto>> Contacts(Guid id, CancellationToken ct) =>
        (await db.CampaignContacts.AsNoTracking()
            .Where(c => c.CampaignId == id)
            .OrderBy(c => c.CreatedAt)
            .Take(ContactCsv.MaxContacts)
            .ToListAsync(ct))
        .Select(c => new CampaignContactDto(
            c.Id, c.PhoneNumber, c.Name, c.Variables, c.Status, c.Attempts, c.NextAttemptAt, c.LastCallId, c.LastResult, c.Outcome, c.Notes, c.UpdatedAt))
        .ToList();

    /// <summary>Agrega contactos desde un CSV; los números que ya están en la campaña se saltean.</summary>
    [HttpPost("api/campaigns/{id:guid}/contacts/import")]
    [Authorize(Roles = Roles.Admin)]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<ImportContactsResult>> Import(Guid id, ImportContactsRequest request, CancellationToken ct)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == id, ct))
        {
            return NotFound();
        }

        var parsed = ContactCsv.Parse(request.Csv);
        var existing = (await db.CampaignContacts.Where(c => c.CampaignId == id).Select(c => c.PhoneNumber).ToListAsync(ct)).ToHashSet();
        var now = time.GetUtcNow();
        var added = 0;
        foreach (var contact in parsed.Contacts.Where(c => !existing.Contains(c.PhoneNumber)))
        {
            db.CampaignContacts.Add(new CampaignContact
            {
                Id = Guid.CreateVersion7(),
                CampaignId = id,
                PhoneNumber = contact.PhoneNumber,
                Name = contact.Name,
                Variables = contact.Variables,
                CreatedAt = now,
            });
            added++;
        }

        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.ContactsImported, $"{added} contactos", id.ToString());
        await events.Clients.All.CampaignsChanged();
        return new ImportContactsResult(added, parsed.Contacts.Count - added, parsed.Problems.Take(50).ToList());
    }

    [HttpDelete("api/campaigns/{id:guid}/contacts/{contactId:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteContact(Guid id, Guid contactId, CancellationToken ct)
    {
        var deleted = await db.CampaignContacts
            .Where(c => c.Id == contactId && c.CampaignId == id && c.Status != CampaignContactStatus.Calling)
            .ExecuteDeleteAsync(ct);
        await events.Clients.All.CampaignsChanged();
        return deleted == 0 ? NotFound() : NoContent();
    }

    /// <summary>Vuelve a poner en cola a un contacto (p. ej. tras "volver a llamar").</summary>
    [HttpPost("api/campaigns/{id:guid}/contacts/{contactId:guid}/retry")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> RetryContact(Guid id, Guid contactId, CancellationToken ct)
    {
        var updated = await db.CampaignContacts
            .Where(c => c.Id == contactId && c.CampaignId == id && c.Status != CampaignContactStatus.Calling)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CampaignContactStatus.Pending)
                .SetProperty(c => c.Attempts, 0)
                .SetProperty(c => c.NextAttemptAt, (DateTimeOffset?)null), ct);
        await events.Clients.All.CampaignsChanged();
        return updated == 0 ? NotFound() : NoContent();
    }

    private async Task<ActionResult?> ValidateAsync(SaveCampaignRequest request, CancellationToken ct)
    {
        if (!await db.Extensions.AnyAsync(e => e.Id == request.ExtensionId, ct))
        {
            ModelState.AddModelError(nameof(SaveCampaignRequest.ExtensionId), "Elige una extensión");
            return ValidationProblem(ModelState);
        }

        return null;
    }

    private static void Apply(Campaign campaign, SaveCampaignRequest request)
    {
        campaign.Name = request.Name.Trim();
        campaign.ExtensionId = request.ExtensionId;
        campaign.Instructions = request.Instructions.Trim();
        campaign.OpeningLine = string.IsNullOrWhiteSpace(request.OpeningLine) ? null : request.OpeningLine.Trim();
        var outcomes = (request.Outcomes ?? []).Select(o => o.Trim()).Where(o => o.Length is > 0 and <= 64).Distinct().Take(12).ToList();
        campaign.Outcomes = outcomes.Count == 0 ? [.. DefaultOutcomes] : outcomes;
        campaign.MaxConcurrentCalls = request.MaxConcurrentCalls;
        campaign.MaxAttempts = request.MaxAttempts;
        campaign.RetryMinutes = request.RetryMinutes;
        campaign.RespectBusinessHours = request.RespectBusinessHours;
    }

    private async Task<Dictionary<Guid, CampaignProgressDto>> ProgressAsync(List<Guid> ids, CancellationToken ct)
    {
        var byStatus = await db.CampaignContacts.AsNoTracking()
            .Where(c => ids.Contains(c.CampaignId))
            .GroupBy(c => new { c.CampaignId, c.Status })
            .Select(g => new { g.Key.CampaignId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        var byOutcome = await db.CampaignContacts.AsNoTracking()
            .Where(c => ids.Contains(c.CampaignId) && c.Outcome != null)
            .GroupBy(c => new { c.CampaignId, c.Outcome })
            .Select(g => new { g.Key.CampaignId, g.Key.Outcome, Count = g.Count() })
            .ToListAsync(ct);

        return ids.ToDictionary(id => id, id =>
        {
            int Count(CampaignContactStatus status) => byStatus.Where(s => s.CampaignId == id && s.Status == status).Sum(s => s.Count);
            return new CampaignProgressDto(
                byStatus.Where(s => s.CampaignId == id).Sum(s => s.Count),
                Count(CampaignContactStatus.Pending),
                Count(CampaignContactStatus.Calling),
                Count(CampaignContactStatus.Completed),
                Count(CampaignContactStatus.NoAnswer),
                Count(CampaignContactStatus.Busy),
                Count(CampaignContactStatus.Failed),
                byOutcome.Where(o => o.CampaignId == id).ToDictionary(o => o.Outcome!, o => o.Count));
        });
    }

    private static CampaignDto ToDto(Campaign c, CampaignProgressDto? progress) => new(
        c.Id, c.Name, c.ExtensionId, c.Extension?.Name ?? "", c.Instructions, c.OpeningLine, c.Outcomes, c.Status,
        c.MaxConcurrentCalls, c.MaxAttempts, c.RetryMinutes, c.RespectBusinessHours, c.CreatedAt, c.StartedAt, c.CompletedAt,
        progress ?? new CampaignProgressDto(0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>()));
}
