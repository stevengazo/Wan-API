using System.ComponentModel.DataAnnotations;
using Mapache.Api.Auth;
using Mapache.Api.Realtime;
using Mapache.Api.Webhooks;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Transfers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record CallMessageDto(
    Guid Id,
    string Recipient,
    Guid? DirectoryEntryId,
    string? DirectoryEntryName,
    string? CallerName,
    string? CallbackNumber,
    string Body,
    bool Urgent,
    CallMessageStatus Status,
    string? ConversationId,
    DateTimeOffset CreatedAt);

public sealed record UpdateMessageRequest(CallMessageStatus Status);

/// <summary>Server tool <c>leave_message</c>. <c>ConversationId</c> se mapea a <c>system__conversation_id</c>.</summary>
public sealed record LeaveMessageToolRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Recipient,
    [Required(ErrorMessage = Messages.Required), StringLength(4000, ErrorMessage = Messages.MaxLength)] string Message,
    [StringLength(128, ErrorMessage = Messages.MaxLength)] string? CallerName = null,
    [StringLength(64, ErrorMessage = Messages.MaxLength)] string? CallbackNumber = null,
    bool Urgent = false,
    string? ConversationId = null);

[ApiController]
public sealed class MessagesController(
    MapacheDbContext db,
    TimeProvider time,
    IHubContext<EventsHub, IEventsClient> events) : ControllerBase
{
    [HttpGet("api/messages")]
    [Authorize]
    public async Task<IReadOnlyList<CallMessageDto>> List(CancellationToken ct) =>
        await db.CallMessages.AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .Take(500)
            .Select(m => new CallMessageDto(
                m.Id, m.Recipient, m.DirectoryEntryId, m.DirectoryEntry!.Name, m.CallerName, m.CallbackNumber,
                m.Body, m.Urgent, m.Status, m.ConversationId, m.CreatedAt))
            .ToListAsync(ct);

    [HttpPatch("api/messages/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Update(Guid id, UpdateMessageRequest request, CancellationToken ct)
    {
        if (await db.CallMessages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, request.Status), ct) == 0)
        {
            return NotFound();
        }

        await events.Clients.All.MessagesChanged();
        return NoContent();
    }

    [HttpDelete("api/messages/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (await db.CallMessages.Where(m => m.Id == id).ExecuteDeleteAsync(ct) == 0)
        {
            return NotFound();
        }

        await events.Clients.All.MessagesChanged();
        return NoContent();
    }

    [HttpPost("api/agent-tools/messages")]
    [Authorize(Policy = AgentToolAuthenticationHandler.PolicyName)]
    public async Task<ToolResult> Leave(LeaveMessageToolRequest request, CancellationToken ct)
    {
        // Se enlaza con el directorio solo si la mejor coincidencia es clara; si no, queda el nombre en texto.
        var directory = await db.DirectoryEntries.AsNoTracking().ToListAsync(ct);
        var match = DirectoryPrompt.Search(directory, request.Recipient, limit: 2);

        db.CallMessages.Add(new CallMessage
        {
            Recipient = request.Recipient.Trim(),
            DirectoryEntryId = match.Count == 1 ? match[0].Id : null,
            CallerName = Optional(request.CallerName),
            CallbackNumber = Optional(request.CallbackNumber),
            Body = request.Message.Trim(),
            Urgent = request.Urgent,
            ConversationId = Optional(request.ConversationId),
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        await events.Clients.All.MessagesChanged();
        var body = request.Message.Trim();
        await events.Clients.All.Notify(new PanelNotification(
            "message",
            request.Urgent ? $"Recado urgente para {request.Recipient.Trim()}" : $"Recado para {request.Recipient.Trim()}",
            body.Length > 140 ? body[..140] + "…" : body,
            "/recados"));
        return new ToolResult(true, "Recado guardado. Dile a quien llama que se lo harán llegar.");
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
