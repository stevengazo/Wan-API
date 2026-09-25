using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Recordings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record ShareMessageRequest([Range(1, 720, ErrorMessage = Messages.Range)] int ExpiresInHours = 24);

public sealed record MessageShareDto(string Token, DateTimeOffset ExpiresAt);

/// <summary>Lo que ve quien abre el enlace: el recado, sin datos internos del panel.</summary>
public sealed record PublicMessageDto(
    string Recipient,
    string? CallerName,
    string? CallbackNumber,
    string Body,
    bool Urgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool HasAudio);

/// <summary>
/// Enlaces públicos de recados para mandarlos por WhatsApp u otro canal. El token es el id del recado
/// firmado con vencimiento (Data Protection): no se guarda nada, vence solo y deja de servir si se borra
/// el recado. Da acceso a ese recado y a su audio, nada más.
/// </summary>
[ApiController]
public sealed class MessageShareController(MapacheDbContext db, RecordingStore store, IDataProtectionProvider protection, TimeProvider time)
    : ControllerBase
{
    private ITimeLimitedDataProtector Protector => protection.CreateProtector("Mapache.MessageShare.v1").ToTimeLimitedDataProtector();

    [HttpPost("api/messages/{id:guid}/share")]
    [Authorize]
    public async Task<ActionResult<MessageShareDto>> Share(Guid id, ShareMessageRequest request, CancellationToken ct)
    {
        if (!await db.CallMessages.AnyAsync(m => m.Id == id, ct))
        {
            return NotFound();
        }

        var expiresAt = time.GetUtcNow().AddHours(request.ExpiresInHours);
        return new MessageShareDto(Protector.Protect(id.ToString("N"), expiresAt), expiresAt);
    }

    [HttpGet("api/public/messages/{token}")]
    [AllowAnonymous]
    public async Task<ActionResult<PublicMessageDto>> Get(string token, CancellationToken ct)
    {
        if (Open(token) is not { } link)
        {
            return Expired();
        }

        var message = await db.CallMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == link.MessageId, ct);
        if (message is null)
        {
            return Expired();
        }

        NoStore();
        var hasAudio = message.ConversationId is not null
            && await db.Recordings.AnyAsync(r => r.ConversationId == message.ConversationId, ct);
        return new PublicMessageDto(
            message.Recipient, message.CallerName, message.CallbackNumber, message.Body, message.Urgent,
            message.CreatedAt, link.ExpiresAt, hasAudio);
    }

    [HttpGet("api/public/messages/{token}/audio")]
    [AllowAnonymous]
    public async Task<IActionResult> Audio(string token, CancellationToken ct)
    {
        if (Open(token) is not { } link)
        {
            return Expired();
        }

        var recording = await db.CallMessages.AsNoTracking()
            .Where(m => m.Id == link.MessageId && m.ConversationId != null)
            .Join(db.Recordings, m => m.ConversationId, r => r.ConversationId, (_, r) => r)
            .FirstOrDefaultAsync(ct);
        if (recording is null || store.PathFor(recording.FileName) is not { } path)
        {
            return NotFound();
        }

        NoStore();
        return PhysicalFile(path, "audio/mpeg", enableRangeProcessing: true);
    }

    private (Guid MessageId, DateTimeOffset ExpiresAt)? Open(string token)
    {
        try
        {
            var id = Protector.Unprotect(token, out var expiresAt);
            return Guid.TryParseExact(id, "N", out var messageId) ? (messageId, expiresAt) : null;
        }
        catch (CryptographicException)
        {
            // Vencido, alterado o de otra instalación: para quien lo abre es lo mismo.
            return null;
        }
    }

    private NotFoundObjectResult Expired() =>
        NotFound(new ProblemDetails { Title = "Este enlace venció o no es válido", Status = StatusCodes.Status404NotFound });

    /// <summary>El recado puede tener datos personales: que no quede en cachés ni en buscadores.</summary>
    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex";
    }
}
