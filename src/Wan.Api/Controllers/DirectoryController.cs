using System.ComponentModel.DataAnnotations;
using Wan.Api.Auth;
using Wan.Api.Webhooks;
using Wan.Domain.Entities;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Transfers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Wan.Api.Controllers;

public sealed record DirectoryEntryDto(
    Guid Id,
    string Name,
    string? Department,
    string Target,
    string? Description,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveDirectoryEntryRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Name,
    [Required(ErrorMessage = Messages.Required), StringLength(256, ErrorMessage = Messages.MaxLength)] string Target,
    [StringLength(128, ErrorMessage = Messages.MaxLength)] string? Department = null,
    [StringLength(1000, ErrorMessage = Messages.MaxLength)] string? Description = null,
    bool Enabled = true);

public sealed record DirectorySearchRequest([Required(ErrorMessage = Messages.Required)] string Query);

/// <summary>Lo que recibe el agente: solo lo necesario para decidir y transferir.</summary>
public sealed record DirectoryMatchDto(string Name, string? Department, string Target, string? Description);

[ApiController]
public sealed class DirectoryController(WanDbContext db, TimeProvider time) : ControllerBase
{
    [HttpGet("api/directory")]
    [Authorize]
    public async Task<IReadOnlyList<DirectoryEntryDto>> List(CancellationToken ct) =>
        await Project(db.DirectoryEntries.OrderBy(d => d.Department).ThenBy(d => d.Name)).ToListAsync(ct);

    [HttpGet("api/directory/{id:guid}")]
    [Authorize]
    public async Task<ActionResult<DirectoryEntryDto>> Get(Guid id, CancellationToken ct) =>
        await Project(db.DirectoryEntries.Where(d => d.Id == id)).SingleOrDefaultAsync(ct) is { } dto ? dto : NotFound();

    [HttpPost("api/directory")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<DirectoryEntryDto>> Create(SaveDirectoryEntryRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var entry = new DirectoryEntry { Name = "", Target = "", CreatedAt = now };
        Apply(entry, request, now);
        db.DirectoryEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        var dto = await Project(db.DirectoryEntries.Where(d => d.Id == entry.Id)).SingleAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = entry.Id }, dto);
    }

    [HttpPut("api/directory/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<DirectoryEntryDto>> Update(Guid id, SaveDirectoryEntryRequest request, CancellationToken ct)
    {
        var entry = await db.DirectoryEntries.FindAsync([id], ct);
        if (entry is null)
        {
            return NotFound();
        }

        Apply(entry, request, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await Project(db.DirectoryEntries.Where(d => d.Id == id)).SingleAsync(ct);
    }

    [HttpDelete("api/directory/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await db.DirectoryEntries.Where(d => d.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();

    /// <summary>
    /// Server tool <c>search_directory</c> del agente: busca destinos activos por nombre, área o tema.
    /// </summary>
    [HttpPost("api/agent-tools/directory/search")]
    [Authorize(Policy = AgentToolAuthenticationHandler.PolicyName)]
    public async Task<IReadOnlyList<DirectoryMatchDto>> Search(DirectorySearchRequest request, CancellationToken ct)
    {
        var entries = await db.DirectoryEntries.AsNoTracking().Where(d => d.Enabled).ToListAsync(ct);
        return DirectoryPrompt.Search(entries, request.Query)
            .Select(e => new DirectoryMatchDto(e.Name, e.Department, e.Target, e.Description))
            .ToList();
    }

    private static IQueryable<DirectoryEntryDto> Project(IQueryable<DirectoryEntry> query) =>
        query.AsNoTracking().Select(d => new DirectoryEntryDto(
            d.Id, d.Name, d.Department, d.Target, d.Description, d.Enabled, d.CreatedAt, d.UpdatedAt));

    private static void Apply(DirectoryEntry entry, SaveDirectoryEntryRequest request, DateTimeOffset now)
    {
        entry.Name = request.Name.Trim();
        entry.Target = request.Target.Trim();
        entry.Department = string.IsNullOrWhiteSpace(request.Department) ? null : request.Department.Trim();
        entry.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        entry.Enabled = request.Enabled;
        entry.UpdatedAt = now;
    }
}
