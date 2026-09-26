using Wan.Api.Audit;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Wan.Api.Agent;
using Wan.Api.Auth;
using Wan.Api.Realtime;
using Wan.Api.Webhooks;
using Wan.Domain.Entities;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Transfers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Wan.Api.Controllers;

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

/// <summary>
/// Server tool <c>leave_message</c>. Los parámetros van en snake_case, como se definen en el agente de
/// ElevenLabs; <c>conversation_id</c> se mapea a <c>system__conversation_id</c>.
/// </summary>
public sealed record LeaveMessageToolRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Recipient,
    [Required(ErrorMessage = Messages.Required), StringLength(4000, ErrorMessage = Messages.MaxLength)] string Message,
    [StringLength(128, ErrorMessage = Messages.MaxLength)][property: JsonPropertyName("caller_name")] string? CallerName = null,
    [StringLength(64, ErrorMessage = Messages.MaxLength)][property: JsonPropertyName("callback_number")] string? CallbackNumber = null,
    bool Urgent = false,
    [property: JsonPropertyName("conversation_id")] string? ConversationId = null);

[ApiController]
public sealed class MessagesController(
    WanDbContext db,
    IHubContext<EventsHub, IEventsClient> events,
    AgentIntake intake,
    AuditLog audit) : ControllerBase
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
        var recipient = await db.CallMessages.Where(m => m.Id == id).Select(m => m.Recipient).SingleOrDefaultAsync(ct);
        if (recipient is null || await db.CallMessages.Where(m => m.Id == id).ExecuteDeleteAsync(ct) == 0)
        {
            return NotFound();
        }

        await audit.RecordAsync(AuditActions.MessageDeleted, $"Recado para {recipient}", id.ToString());
        await events.Clients.All.MessagesChanged();
        return NoContent();
    }

    [HttpPost("api/agent-tools/messages")]
    [Authorize(Policy = AgentToolAuthenticationHandler.PolicyName)]
    public Task<ToolResult> Leave(LeaveMessageToolRequest request, CancellationToken ct) =>
        intake.LeaveMessageAsync(request, ct);

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
