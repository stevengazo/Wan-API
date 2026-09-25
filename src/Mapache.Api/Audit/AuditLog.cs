using System.Security.Claims;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;

namespace Mapache.Api.Audit;

/// <summary>Códigos de las acciones auditadas (el panel los traduce).</summary>
public static class AuditActions
{
    public const string LoginSucceeded = "auth.login";
    public const string LoginFailed = "auth.login_failed";
    public const string PasswordChanged = "auth.password_changed";
    public const string MfaEnabled = "auth.mfa_enabled";
    public const string MfaDisabled = "auth.mfa_disabled";
    public const string ExternalLinked = "auth.external_linked";
    public const string ExternalUnlinked = "auth.external_unlinked";
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserPasswordReset = "user.password_reset";
    public const string UserDeleted = "user.deleted";
    public const string RecordingPlayed = "recording.played";
    public const string RecordingDeleted = "recording.deleted";
    public const string MessageShared = "message.shared";
    public const string MessagePublicViewed = "message.public_viewed";
    public const string MessageDeleted = "message.deleted";
    public const string SettingsChanged = "settings.changed";
    public const string McpTokenCreated = "mcp_token.created";
    public const string McpTokenChanged = "mcp_token.changed";
    public const string McpTokenRevoked = "mcp_token.revoked";
    public const string CampaignStarted = "campaign.started";
    public const string CampaignPaused = "campaign.paused";
    public const string CampaignDeleted = "campaign.deleted";
    public const string ContactsImported = "campaign.contacts_imported";
    public const string RetentionPurged = "retention.purged";
}

/// <summary>
/// Escribe eventos de auditoría con el usuario y la IP de la petición actual. Se guarda en el momento, aparte de
/// los cambios de la petición: si la acción falla después, el intento igual queda registrado.
/// </summary>
public sealed class AuditLog(IServiceScopeFactory scopes, IHttpContextAccessor http, TimeProvider time, ILogger<AuditLog> logger)
{
    public async Task RecordAsync(
        string action, string? target = null, string? targetId = null, string? details = null, string? userEmail = null, Guid? userId = null)
    {
        var context = http.HttpContext;
        var user = context?.User;
        var entry = new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            At = time.GetUtcNow(),
            UserId = Guid.TryParse(user?.FindFirstValue("sub"), out var id) ? id : userId,
            UserEmail = user?.FindFirstValue("email") ?? userEmail,
            Action = action,
            Target = Truncate(target, 256),
            TargetId = Truncate(targetId, 64),
            IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
            Details = Truncate(details, 1000),
        };

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MapacheDbContext>();
            db.AuditEvents.Add(entry);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Que no se pueda auditar es grave, pero no debe tumbar la acción del usuario.
            logger.LogError(ex, "No se pudo registrar el evento de auditoría {Action}", action);
        }
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
