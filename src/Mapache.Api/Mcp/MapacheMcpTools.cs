using System.ComponentModel;
using System.Text.Json;
using Mapache.Api.Realtime;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Knowledge;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Transfers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Mapache.Api.Mcp;

/// <summary>
/// Herramientas del servidor MCP de Mapache (<c>/api/mcp</c>) para agentes externos. Devuelven JSON compacto:
/// lo leen modelos, no personas.
/// </summary>
[McpServerToolType]
public sealed class MapacheMcpTools(
    MapacheDbContext db,
    KnowledgeService knowledge,
    IHubContext<EventsHub, IEventsClient> events)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "search_knowledge_base", ReadOnly = true)]
    [Description("Busca en las bases de conocimiento activas de Mapache (políticas, precios, horarios). Devuelve los fragmentos más relevantes con su documento.")]
    public async Task<string> SearchKnowledgeBase(
        [Description("Qué buscar, en lenguaje natural")] string query,
        CancellationToken ct)
    {
        var hits = await knowledge.SearchAsync(query, limit: 5, ct: ct);
        return JsonSerializer.Serialize(hits.Select(h => new { h.BaseName, h.DocumentName, h.Text }), Json);
    }

    [McpServerTool(Name = "search_directory", ReadOnly = true)]
    [Description("Busca en el directorio de transferencias por nombre, área o tema. Devuelve a qué extensión o número transferir.")]
    public async Task<string> SearchDirectory([Description("Nombre, área o tema")] string query, CancellationToken ct)
    {
        var entries = await db.DirectoryEntries.AsNoTracking().Where(d => d.Enabled).ToListAsync(ct);
        return JsonSerializer.Serialize(DirectoryPrompt.Search(entries, query).Select(e => new { e.Name, e.Department, e.Target, e.Description }), Json);
    }

    [McpServerTool(Name = "list_messages", ReadOnly = true)]
    [Description("Lista los recados que tomó el bot, del más reciente al más antiguo.")]
    public async Task<string> ListMessages(
        [Description("pending (nuevos y leídos, por defecto), new, done o all")] string status = "pending",
        [Description("Cuántos devolver, máximo 100")] int limit = 20,
        CancellationToken ct = default)
    {
        var query = db.CallMessages.AsNoTracking();
        query = status switch
        {
            "new" => query.Where(m => m.Status == CallMessageStatus.New),
            "done" => query.Where(m => m.Status == CallMessageStatus.Done),
            "all" => query,
            _ => query.Where(m => m.Status != CallMessageStatus.Done),
        };
        var messages = await query.OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(limit, 1, 100))
            .Select(m => new { m.Id, m.Recipient, m.CallerName, m.CallbackNumber, m.Body, m.Urgent, Status = m.Status.ToString(), m.CreatedAt })
            .ToListAsync(ct);
        return JsonSerializer.Serialize(messages, Json);
    }

    [McpServerTool(Name = "update_message_status", Idempotent = true)]
    [Description("Cambia el estado de un recado: new, read o done.")]
    public async Task<string> UpdateMessageStatus(
        [Description("Id del recado (de list_messages)")] Guid id,
        [Description("new, read o done")] string status,
        CancellationToken ct)
    {
        if (!Enum.TryParse<CallMessageStatus>(status, ignoreCase: true, out var value))
        {
            return "Estado inválido: usa new, read o done.";
        }

        var updated = await db.CallMessages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, value), ct);
        if (updated == 0)
        {
            return "No existe un recado con ese id.";
        }

        await events.Clients.All.MessagesChanged();
        return $"Recado marcado como {value}.";
    }

    [McpServerTool(Name = "list_forms", ReadOnly = true)]
    [Description("Lista los formularios que llena el bot, con sus campos y cuántas respuestas nuevas tienen.")]
    public async Task<string> ListForms(CancellationToken ct)
    {
        var forms = await db.FormTemplates.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
        var pending = await db.FormSubmissions.AsNoTracking()
            .Where(s => s.Status == SubmissionStatus.New)
            .GroupBy(s => s.FormTemplateId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return JsonSerializer.Serialize(forms.Select(f => new
        {
            f.Id,
            f.Name,
            f.Description,
            f.Enabled,
            Fields = f.Fields.Select(x => new { x.Key, x.Label, Type = x.Type.ToString(), x.Required }),
            NewSubmissions = pending.GetValueOrDefault(f.Id),
        }), Json);
    }

    [McpServerTool(Name = "list_form_submissions", ReadOnly = true)]
    [Description("Respuestas de un formulario, de la más reciente a la más antigua.")]
    public async Task<string> ListFormSubmissions(
        [Description("Id o nombre exacto del formulario (de list_forms)")] string form,
        [Description("Solo las no revisadas")] bool onlyNew = true,
        [Description("Cuántas devolver, máximo 100")] int limit = 20,
        CancellationToken ct = default)
    {
        var template = Guid.TryParse(form, out var id)
            ? await db.FormTemplates.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct)
            : await db.FormTemplates.AsNoTracking().FirstOrDefaultAsync(f => f.Name == form, ct);
        if (template is null)
        {
            return "No existe ese formulario; usa list_forms para ver los nombres.";
        }

        var submissions = await db.FormSubmissions.AsNoTracking()
            .Where(s => s.FormTemplateId == template.Id && (!onlyNew || s.Status == SubmissionStatus.New))
            .OrderByDescending(s => s.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(ct);
        return JsonSerializer.Serialize(submissions.Select(s => new { s.Id, s.Values, s.CallerNumber, Status = s.Status.ToString(), s.CreatedAt }), Json);
    }
}
