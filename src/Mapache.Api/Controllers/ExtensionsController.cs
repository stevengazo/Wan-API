using System.ComponentModel.DataAnnotations;
using Mapache.Api.Auth;
using Mapache.Api.Realtime;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Security;
using Mapache.Sip.Engine;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

/// <summary>La contraseña SIP nunca sale de la API; solo se informa si está configurada.</summary>
public sealed record ExtensionDto(
    Guid Id,
    string Name,
    string SipServer,
    string? SecondarySipServer,
    string? SipProxy,
    string SipUsername,
    string? SipDomain,
    string? AuthUsername,
    string? DisplayName,
    SipTransport Transport,
    string? PublicAddress,
    string? StunServer,
    int RegisterExpirySeconds,
    int RegisterRetrySeconds,
    int KeepAliveSeconds,
    int LocalPort,
    bool AllowIpRewrite,
    bool UseIce,
    MediaEncryption MediaEncryption,
    IReadOnlyList<string> Codecs,
    DtmfMode DtmfMode,
    int SessionTimerSeconds,
    int MaxConcurrentCalls,
    string? VoicemailNumber,
    string? DialPrefix,
    bool HideCallerId,
    AnswerMode AnswerMode,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Los mismos campos que la cuenta de MicroSIP. <c>SipPassword</c> es obligatoria al crear;
/// en edición, nula o vacía conserva la actual.
/// </summary>
public sealed record SaveExtensionRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Name,
    [Required(ErrorMessage = Messages.Required), StringLength(256, ErrorMessage = Messages.MaxLength)] string SipServer,
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string SipUsername,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? SipPassword,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? SecondarySipServer = null,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? SipProxy = null,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? SipDomain = null,
    [StringLength(128, ErrorMessage = Messages.MaxLength)] string? AuthUsername = null,
    [StringLength(128, ErrorMessage = Messages.MaxLength)] string? DisplayName = null,
    SipTransport Transport = SipTransport.Udp,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? PublicAddress = null,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? StunServer = null,
    [Range(60, 3600, ErrorMessage = Messages.Range)] int RegisterExpirySeconds = 300,
    [Range(5, 3600, ErrorMessage = Messages.Range)] int RegisterRetrySeconds = 30,
    [Range(0, 300, ErrorMessage = Messages.Range)] int KeepAliveSeconds = 15,
    [Range(0, 65535, ErrorMessage = Messages.Range)] int LocalPort = 0,
    bool AllowIpRewrite = true,
    bool UseIce = false,
    MediaEncryption MediaEncryption = MediaEncryption.Disabled,
    IReadOnlyList<string>? Codecs = null,
    DtmfMode DtmfMode = DtmfMode.Rfc2833,
    [Range(0, 7200, ErrorMessage = Messages.Range)] int SessionTimerSeconds = 1800,
    [Range(0, 100, ErrorMessage = Messages.Range)] int MaxConcurrentCalls = 0,
    [StringLength(64, ErrorMessage = Messages.MaxLength)] string? VoicemailNumber = null,
    [StringLength(32, ErrorMessage = Messages.MaxLength)] string? DialPrefix = null,
    bool HideCallerId = false,
    AnswerMode AnswerMode = AnswerMode.Bot,
    bool Enabled = true);

[ApiController]
[Route("api/extensions")]
[Authorize]
public sealed class ExtensionsController(
    MapacheDbContext db,
    ISecretProtector protector,
    TimeProvider time,
    ISipEngine engine,
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
    public async Task<ActionResult<ExtensionDto>> Create(SaveExtensionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.SipPassword))
        {
            ModelState.AddModelError(nameof(SaveExtensionRequest.SipPassword), Messages.Required);
            return ValidationProblem(ModelState);
        }

        if (await ValidateAsync(null, request, ct) is { } error)
        {
            return error;
        }

        var now = time.GetUtcNow();
        var extension = new Extension
        {
            Name = "",
            SipServer = "",
            SipUsername = "",
            SipPasswordEncrypted = protector.Protect(request.SipPassword),
            CreatedAt = now,
        };
        Apply(extension, request, now);
        db.Extensions.Add(extension);
        await db.SaveChangesAsync(ct);
        engine.RequestReload();
        await events.Clients.All.ExtensionsChanged();

        var dto = await Project(db.Extensions.Where(e => e.Id == extension.Id)).SingleAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = extension.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<ExtensionDto>> Update(Guid id, SaveExtensionRequest request, CancellationToken ct)
    {
        var extension = await db.Extensions.FindAsync([id], ct);
        if (extension is null)
        {
            return NotFound();
        }

        if (await ValidateAsync(id, request, ct) is { } error)
        {
            return error;
        }

        Apply(extension, request, time.GetUtcNow());
        if (!string.IsNullOrEmpty(request.SipPassword))
        {
            extension.SipPasswordEncrypted = protector.Protect(request.SipPassword);
        }

        await db.SaveChangesAsync(ct);
        engine.RequestReload();
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

        engine.RequestReload();
        await events.Clients.All.ExtensionsChanged();
        return NoContent();
    }

    // Filtrar y ordenar antes de proyectar: EF no traduce predicados sobre un record construido.
    private static IQueryable<ExtensionDto> Project(IQueryable<Extension> query) =>
        query.AsNoTracking().Select(e => new ExtensionDto(
            e.Id, e.Name, e.SipServer, e.SecondarySipServer, e.SipProxy, e.SipUsername, e.SipDomain, e.AuthUsername,
            e.DisplayName, e.Transport, e.PublicAddress, e.StunServer, e.RegisterExpirySeconds, e.RegisterRetrySeconds,
            e.KeepAliveSeconds, e.LocalPort, e.AllowIpRewrite, e.UseIce, e.MediaEncryption, e.Codecs, e.DtmfMode,
            e.SessionTimerSeconds, e.MaxConcurrentCalls, e.VoicemailNumber, e.DialPrefix, e.HideCallerId,
            e.AnswerMode, e.Enabled, e.CreatedAt, e.UpdatedAt));

    /// <summary>Copia todo salvo la contraseña, que crear y editar tratan distinto.</summary>
    private static void Apply(Extension extension, SaveExtensionRequest request, DateTimeOffset now)
    {
        extension.Name = request.Name.Trim();
        extension.SipServer = request.SipServer.Trim();
        extension.SecondarySipServer = Optional(request.SecondarySipServer);
        extension.SipProxy = Optional(request.SipProxy);
        extension.SipUsername = request.SipUsername.Trim();
        extension.SipDomain = Optional(request.SipDomain);
        extension.AuthUsername = Optional(request.AuthUsername);
        extension.DisplayName = Optional(request.DisplayName);
        extension.Transport = request.Transport;
        extension.PublicAddress = Optional(request.PublicAddress);
        extension.StunServer = Optional(request.StunServer);
        extension.RegisterExpirySeconds = request.RegisterExpirySeconds;
        extension.RegisterRetrySeconds = request.RegisterRetrySeconds;
        extension.KeepAliveSeconds = request.KeepAliveSeconds;
        extension.LocalPort = request.LocalPort;
        extension.AllowIpRewrite = request.AllowIpRewrite;
        extension.UseIce = request.UseIce;
        extension.MediaEncryption = request.MediaEncryption;
        extension.Codecs = NormalizeCodecs(request.Codecs);
        extension.DtmfMode = request.DtmfMode;
        extension.SessionTimerSeconds = request.SessionTimerSeconds;
        extension.MaxConcurrentCalls = request.MaxConcurrentCalls;
        extension.VoicemailNumber = Optional(request.VoicemailNumber);
        extension.DialPrefix = Optional(request.DialPrefix);
        extension.HideCallerId = request.HideCallerId;
        extension.AnswerMode = request.AnswerMode;
        extension.Enabled = request.Enabled;
        extension.UpdatedAt = now;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Sin lista se usan los códecs por defecto; los nombres se llevan a su forma canónica.</summary>
    private static List<string> NormalizeCodecs(IReadOnlyList<string>? codecs) =>
        codecs is null or { Count: 0 }
            ? ["PCMU", "PCMA"]
            : codecs.Select(c => Extension.SupportedCodecs.First(s => s.Equals(c.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Distinct()
                .ToList();

    private async Task<ActionResult?> ValidateAsync(Guid? id, SaveExtensionRequest request, CancellationToken ct)
    {
        if (request.Codecs?.FirstOrDefault(c => !Extension.SupportedCodecs.Contains(c.Trim(), StringComparer.OrdinalIgnoreCase)) is { } unknown)
        {
            ModelState.AddModelError(nameof(SaveExtensionRequest.Codecs), $"Códec no soportado: {unknown}");
            return ValidationProblem(ModelState);
        }

        var server = request.SipServer.Trim();
        var username = request.SipUsername.Trim();
        if (await db.Extensions.AnyAsync(e => e.SipServer == server && e.SipUsername == username && e.Id != id, ct))
        {
            ModelState.AddModelError(nameof(SaveExtensionRequest.SipUsername), "Ya existe una cuenta con ese usuario en este servidor");
            return ValidationProblem(ModelState);
        }

        return null;
    }
}
