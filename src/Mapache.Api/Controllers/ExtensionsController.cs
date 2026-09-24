using System.ComponentModel.DataAnnotations;
using Mapache.Api.Auth;
using Mapache.Api.Realtime;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

/// <summary>La contraseña SIP nunca sale de la API; solo se informa si está configurada.</summary>
public sealed record ExtensionDto(
    Guid Id,
    string Name,
    Guid ProviderId,
    string ProviderName,
    string SipUsername,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateExtensionRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Name,
    [Required(ErrorMessage = Messages.Required)] Guid ProviderId,
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string SipUsername,
    [Required(ErrorMessage = Messages.Required), StringLength(256, ErrorMessage = Messages.MaxLength)] string SipPassword,
    bool Enabled = true);

/// <summary><c>SipPassword</c> nulo o vacío conserva la contraseña actual.</summary>
public sealed record UpdateExtensionRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Name,
    [Required(ErrorMessage = Messages.Required)] Guid ProviderId,
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string SipUsername,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? SipPassword,
    bool Enabled);

[ApiController]
[Route("api/extensions")]
[Authorize]
public sealed class ExtensionsController(
    MapacheDbContext db,
    ISecretProtector protector,
    TimeProvider time,
    IHubContext<EventsHub, IEventsClient> events) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ExtensionDto>> List(CancellationToken ct) =>
        await Project(db.Extensions.OrderBy(e => e.Name)).ToListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ExtensionDto>> Get(Guid id, CancellationToken ct) =>
        await Project(db.Extensions.Where(e => e.Id == id)).SingleOrDefaultAsync(ct) is { } dto ? dto : NotFound();

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<ExtensionDto>> Create(CreateExtensionRequest request, CancellationToken ct)
    {
        if (await ValidateAsync(null, request.ProviderId, request.SipUsername, ct) is { } error)
        {
            return error;
        }

        var now = time.GetUtcNow();
        var extension = new Extension
        {
            Name = request.Name.Trim(),
            ProviderId = request.ProviderId,
            SipUsername = request.SipUsername.Trim(),
            SipPasswordEncrypted = protector.Protect(request.SipPassword),
            Enabled = request.Enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Extensions.Add(extension);
        await db.SaveChangesAsync(ct);
        await events.Clients.All.ExtensionsChanged();

        var dto = await Project(db.Extensions.Where(e => e.Id == extension.Id)).SingleAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = extension.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<ExtensionDto>> Update(Guid id, UpdateExtensionRequest request, CancellationToken ct)
    {
        var extension = await db.Extensions.FindAsync([id], ct);
        if (extension is null)
        {
            return NotFound();
        }

        if (await ValidateAsync(id, request.ProviderId, request.SipUsername, ct) is { } error)
        {
            return error;
        }

        extension.Name = request.Name.Trim();
        extension.ProviderId = request.ProviderId;
        extension.SipUsername = request.SipUsername.Trim();
        extension.Enabled = request.Enabled;
        if (!string.IsNullOrEmpty(request.SipPassword))
        {
            extension.SipPasswordEncrypted = protector.Protect(request.SipPassword);
        }

        extension.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await events.Clients.All.ExtensionsChanged();
        return await Project(db.Extensions.Where(e => e.Id == id)).SingleAsync(ct);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (await db.Calls.AnyAsync(c => c.ExtensionId == id, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: "La extensión tiene llamadas registradas; deshabilítala en lugar de borrarla");
        }

        var deleted = await db.Extensions.Where(e => e.Id == id).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return NotFound();
        }

        await events.Clients.All.ExtensionsChanged();
        return NoContent();
    }

    // Filtrar y ordenar antes de proyectar: EF no traduce predicados sobre un record construido.
    private static IQueryable<ExtensionDto> Project(IQueryable<Extension> query) =>
        query.AsNoTracking().Select(e => new ExtensionDto(
            e.Id, e.Name, e.ProviderId, e.Provider!.Name, e.SipUsername, e.Enabled, e.CreatedAt, e.UpdatedAt));

    private async Task<ActionResult?> ValidateAsync(Guid? id, Guid providerId, string sipUsername, CancellationToken ct)
    {
        if (!await db.Providers.AnyAsync(p => p.Id == providerId, ct))
        {
            ModelState.AddModelError(nameof(CreateExtensionRequest.ProviderId), "El proveedor no existe");
            return ValidationProblem(ModelState);
        }

        var username = sipUsername.Trim();
        if (await db.Extensions.AnyAsync(e => e.ProviderId == providerId && e.SipUsername == username && e.Id != id, ct))
        {
            ModelState.AddModelError(nameof(CreateExtensionRequest.SipUsername), "Ya existe una extensión con ese usuario SIP en este proveedor");
            return ValidationProblem(ModelState);
        }

        return null;
    }
}
