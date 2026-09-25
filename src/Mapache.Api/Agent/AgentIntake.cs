using Mapache.Api.Controllers;
using Mapache.Api.Realtime;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Actions;
using Mapache.Infrastructure.Forms;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Tools;
using Mapache.Infrastructure.Transfers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Agent;

/// <summary>
/// Recados y formularios que toma el bot. Los usan las server tools de ElevenLabs (controllers) y las
/// herramientas internas de Mapache (<see cref="IntakeToolSource"/>) para los demás proveedores de voz.
/// </summary>
public sealed class AgentIntake(
    MapacheDbContext db,
    TimeProvider time,
    IFormActionQueue actionQueue,
    IHubContext<EventsHub, IEventsClient> events)
{
    public async Task<ToolResult> LeaveMessageAsync(LeaveMessageToolRequest request, CancellationToken ct)
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

    public async Task<ToolResult> SubmitFormAsync(SubmitFormToolRequest request, CancellationToken ct)
    {
        var forms = await db.FormTemplates.AsNoTracking().Where(f => f.Enabled).ToListAsync(ct);
        if (FormsPrompt.Find(forms, request.Form) is not { } form)
        {
            return new ToolResult(false, $"No existe el formulario \"{request.Form}\". Usa el nombre exacto de la lista.");
        }

        var validation = FormsPrompt.Validate(form, request.Values);
        if (!validation.IsValid)
        {
            return new ToolResult(false, "No se guardó. Pide o corrige estos datos y vuelve a enviarlo.", validation.Problems);
        }

        var submission = new FormSubmission
        {
            FormTemplateId = form.Id,
            Values = validation.Values,
            ConversationId = Optional(request.ConversationId),
            CallerNumber = Optional(request.CallerNumber),
            CreatedAt = time.GetUtcNow(),
        };
        db.FormSubmissions.Add(submission);
        await db.SaveChangesAsync(ct);
        // Las acciones (webhooks, Teams, correo...) van a la cola: el bot no espera a que terminen.
        await FormActionRunner.DispatchAsync(db, actionQueue, submission, submission.CreatedAt, ct);
        await events.Clients.All.FormSubmissionsChanged();
        await events.Clients.All.Notify(new PanelNotification(
            "form",
            $"Nuevo formulario: {form.Name}",
            string.Join(" · ", validation.Values.Take(3).Select(v => v.Value)),
            $"/formularios/{form.Id}"));
        return new ToolResult(true, "Formulario guardado. Confírmale a quien llama que quedó registrado.");
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// <c>leave_message</c> y <c>submit_form</c> como herramientas de Mapache: funcionan con cualquier proveedor
/// de voz sin configurar server tools. El número y la conversación salen de la llamada, no del modelo.
/// </summary>
public sealed class IntakeToolSource(MapacheDbContext db, AgentIntake intake) : IBotToolSource
{
    public async Task<IReadOnlyList<BotTool>> GetToolsAsync(BotCallContext call, CancellationToken ct)
    {
        var current = call.CallId is { } callId
            ? await db.Calls.AsNoTracking().Where(c => c.Id == callId).Select(c => new { c.CallerNumber, c.ElevenLabsConversationId }).SingleOrDefaultAsync(ct)
            : null;
        var tools = new List<BotTool>
        {
            new(
                "leave_message",
                "Guarda un recado para alguien que no puede atender. Confirma los datos con quien llama antes de guardarlo.",
                ToolSchema.Object(
                    ("recipient", "string", "Para quién es el recado", true),
                    ("message", "string", "El recado, con las palabras de quien llama", true),
                    ("caller_name", "string", "Nombre de quien llama", false),
                    ("callback_number", "string", "Número para devolver la llamada", false),
                    ("urgent", "boolean", "Solo si quien llama lo pide o es claramente urgente", false)),
                async (args, token) => Describe(await intake.LeaveMessageAsync(
                    new LeaveMessageToolRequest(
                        Text(args, "recipient") ?? "",
                        Text(args, "message") ?? "",
                        Text(args, "caller_name"),
                        Text(args, "callback_number") ?? current?.CallerNumber,
                        args["urgent"]?.GetValue<bool>() ?? false,
                        current?.ElevenLabsConversationId),
                    token))),
        };

        if (await db.FormTemplates.AnyAsync(f => f.Enabled, ct))
        {
            tools.Add(new(
                "submit_form",
                "Guarda un formulario completo con los datos que dio quien llama. Usa el nombre exacto del formulario.",
                new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["form"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string", ["description"] = "Nombre exacto del formulario" },
                        ["values"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "object",
                            ["description"] = "Clave del campo → valor",
                            ["additionalProperties"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
                        },
                    },
                    ["required"] = new System.Text.Json.Nodes.JsonArray("form", "values"),
                },
                async (args, token) =>
                {
                    var values = (args["values"] as System.Text.Json.Nodes.JsonObject ?? [])
                        .ToDictionary(v => v.Key, v => v.Value is System.Text.Json.Nodes.JsonValue value ? value.ToString() : v.Value?.ToJsonString());
                    return Describe(await intake.SubmitFormAsync(
                        new SubmitFormToolRequest(Text(args, "form") ?? "", values, current?.ElevenLabsConversationId, current?.CallerNumber),
                        token));
                }));
        }

        return tools;
    }

    private static string? Text(System.Text.Json.Nodes.JsonObject args, string name) =>
        args[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string Describe(ToolResult result) =>
        result.Problems is { Count: > 0 } problems ? $"{result.Message} {string.Join("; ", problems)}" : result.Message;
}
