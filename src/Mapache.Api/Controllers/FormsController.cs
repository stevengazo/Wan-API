using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Mapache.Api.Auth;
using Mapache.Api.Realtime;
using Mapache.Api.Webhooks;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Actions;
using Mapache.Infrastructure.Forms;
using Mapache.Infrastructure.Llm;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record FormFieldDto(string Key, string Label, FormFieldType Type, bool Required, string? Hint, IReadOnlyList<string> Options);

public sealed record FormTemplateDto(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<FormFieldDto> Fields,
    bool Enabled,
    int SubmissionCount,
    int NewSubmissionCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveFormTemplateRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Name,
    [StringLength(1000, ErrorMessage = Messages.MaxLength)] string? Description,
    [Required(ErrorMessage = Messages.Required)] IReadOnlyList<FormFieldDto> Fields,
    bool Enabled = true);

public sealed record ActionExecutionDto(
    Guid Id,
    Guid FormActionId,
    string ActionName,
    FormActionType ActionType,
    ActionExecutionStatus Status,
    int Attempts,
    string? LastError,
    DateTimeOffset? CompletedAt);

public sealed record FormSubmissionDto(
    Guid Id,
    Guid FormTemplateId,
    IReadOnlyDictionary<string, string> Values,
    string? ConversationId,
    string? CallerNumber,
    SubmissionStatus Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ActionExecutionDto> Actions);

public sealed record UpdateSubmissionRequest(SubmissionStatus Status);

public sealed record GenerateFormRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(2000, MinimumLength = 10, ErrorMessage = "Entre {2} y {1} caracteres")] string Description);

/// <summary>Borrador generado por la IA: el panel lo carga en el editor, no se guarda solo.</summary>
public sealed record FormDraftDto(string Name, string? Description, IReadOnlyList<FormFieldDto> Fields);

/// <summary>
/// Server tool <c>submit_form</c>. <c>ConversationId</c> y <c>CallerNumber</c> se mapean en el agente a las
/// variables de sistema <c>system__conversation_id</c> y <c>system__caller_id</c>.
/// </summary>
public sealed record SubmitFormToolRequest(
    [Required(ErrorMessage = Messages.Required)] string Form,
    [Required(ErrorMessage = Messages.Required)] Dictionary<string, string?> Values,
    string? ConversationId = null,
    string? CallerNumber = null);

/// <summary>Respuesta para el bot: siempre 200, con lo que falta si no se pudo guardar.</summary>
public sealed record ToolResult(bool Ok, string Message, IReadOnlyList<string>? Problems = null);

[ApiController]
public sealed partial class FormsController(
    MapacheDbContext db,
    TimeProvider time,
    IFormActionQueue actionQueue,
    IHubContext<EventsHub, IEventsClient> events) : ControllerBase
{
    [HttpGet("api/forms")]
    [Authorize]
    public async Task<IReadOnlyList<FormTemplateDto>> List(CancellationToken ct)
    {
        var forms = await db.FormTemplates.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
        var counts = await db.FormSubmissions.AsNoTracking()
            .GroupBy(s => s.FormTemplateId)
            .Select(g => new { g.Key, Total = g.Count(), New = g.Count(s => s.Status == SubmissionStatus.New) })
            .ToDictionaryAsync(x => x.Key, ct);
        return forms.Select(f => ToDto(f, counts.GetValueOrDefault(f.Id)?.Total ?? 0, counts.GetValueOrDefault(f.Id)?.New ?? 0)).ToList();
    }

    [HttpGet("api/forms/{id:guid}")]
    [Authorize]
    public async Task<ActionResult<FormTemplateDto>> Get(Guid id, CancellationToken ct)
    {
        var form = await db.FormTemplates.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct);
        if (form is null)
        {
            return NotFound();
        }

        var total = await db.FormSubmissions.CountAsync(s => s.FormTemplateId == id, ct);
        var pending = await db.FormSubmissions.CountAsync(s => s.FormTemplateId == id && s.Status == SubmissionStatus.New, ct);
        return ToDto(form, total, pending);
    }

    [HttpPost("api/forms")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<FormTemplateDto>> Create(SaveFormTemplateRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error)
        {
            return error;
        }

        var now = time.GetUtcNow();
        var form = new FormTemplate { Name = "", CreatedAt = now };
        Apply(form, request, now);
        db.FormTemplates.Add(form);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = form.Id }, ToDto(form, 0, 0));
    }

    [HttpPost("api/forms/generate")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<FormDraftDto>> Generate(GenerateFormRequest request, [FromServices] FormGenerator generator, CancellationToken ct)
    {
        try
        {
            var draft = await generator.GenerateAsync(request.Description, ct);
            return new FormDraftDto(
                draft.Name,
                draft.Description,
                draft.Fields.Select(f => new FormFieldDto(f.Key, f.Label, f.Type, f.Required, f.Hint, f.Options)).ToList());
        }
        catch (LlmException ex)
        {
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: ex.Message);
        }
    }

    [HttpPut("api/forms/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<FormTemplateDto>> Update(Guid id, SaveFormTemplateRequest request, CancellationToken ct)
    {
        var form = await db.FormTemplates.FindAsync([id], ct);
        if (form is null)
        {
            return NotFound();
        }

        if (Validate(request) is { } error)
        {
            return error;
        }

        Apply(form, request, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>Borra también sus respuestas.</summary>
    [HttpDelete("api/forms/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await db.FormTemplates.Where(f => f.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();

    [HttpGet("api/forms/{id:guid}/submissions")]
    [Authorize]
    public async Task<IReadOnlyList<FormSubmissionDto>> Submissions(Guid id, CancellationToken ct)
    {
        var submissions = await db.FormSubmissions.AsNoTracking()
            .Where(s => s.FormTemplateId == id)
            .OrderByDescending(s => s.CreatedAt)
            .Take(500)
            .ToListAsync(ct);
        var ids = submissions.Select(s => s.Id).ToList();
        var executions = (await db.ActionExecutions.AsNoTracking()
                .Where(x => ids.Contains(x.FormSubmissionId))
                .OrderBy(x => x.CreatedAt)
                .Select(x => new { x.FormSubmissionId, Dto = new ActionExecutionDto(
                    x.Id, x.FormActionId, x.FormAction!.Name, x.FormAction.Type, x.Status, x.Attempts, x.LastError, x.CompletedAt) })
                .ToListAsync(ct))
            .ToLookup(x => x.FormSubmissionId, x => x.Dto);
        return submissions.Select(s => ToDto(s, executions[s.Id].ToList())).ToList();
    }

    [HttpPatch("api/form-submissions/{id:guid}")]
    [Authorize]
    public async Task<ActionResult<FormSubmissionDto>> UpdateSubmission(Guid id, UpdateSubmissionRequest request, CancellationToken ct)
    {
        var submission = await db.FormSubmissions.FindAsync([id], ct);
        if (submission is null)
        {
            return NotFound();
        }

        submission.Status = request.Status;
        await db.SaveChangesAsync(ct);
        await events.Clients.All.FormSubmissionsChanged();
        return ToDto(submission, []);
    }

    [HttpDelete("api/form-submissions/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteSubmission(Guid id, CancellationToken ct)
    {
        if (await db.FormSubmissions.Where(s => s.Id == id).ExecuteDeleteAsync(ct) == 0)
        {
            return NotFound();
        }

        await events.Clients.All.FormSubmissionsChanged();
        return NoContent();
    }

    [HttpPost("api/agent-tools/forms/submit")]
    [Authorize(Policy = AgentToolAuthenticationHandler.PolicyName)]
    public async Task<ToolResult> Submit(SubmitFormToolRequest request, CancellationToken ct)
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
        return new ToolResult(true, "Formulario guardado. Confírmale a quien llama que quedó registrado.");
    }

    private ActionResult? Validate(SaveFormTemplateRequest request)
    {
        if (request.Fields.Count == 0)
        {
            ModelState.AddModelError(nameof(SaveFormTemplateRequest.Fields), "Agrega al menos un campo");
        }

        foreach (var (field, index) in request.Fields.Select((f, i) => (f, i)))
        {
            var prefix = $"{nameof(SaveFormTemplateRequest.Fields)}[{index}]";
            if (string.IsNullOrWhiteSpace(field.Label))
            {
                ModelState.AddModelError($"{prefix}.{nameof(FormFieldDto.Label)}", Messages.Required);
            }

            if (!KeyPattern().IsMatch(field.Key ?? ""))
            {
                ModelState.AddModelError($"{prefix}.{nameof(FormFieldDto.Key)}", "Solo minúsculas, números y guion bajo");
            }

            if (field.Type == FormFieldType.Choice && !(field.Options?.Any(o => !string.IsNullOrWhiteSpace(o)) ?? false))
            {
                ModelState.AddModelError($"{prefix}.{nameof(FormFieldDto.Options)}", "Agrega al menos una opción");
            }
        }

        if (request.Fields.GroupBy(f => f.Key).FirstOrDefault(g => g.Count() > 1) is { } duplicated)
        {
            ModelState.AddModelError(nameof(SaveFormTemplateRequest.Fields), $"El campo \"{duplicated.Key}\" está repetido");
        }

        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }

    private static void Apply(FormTemplate form, SaveFormTemplateRequest request, DateTimeOffset now)
    {
        form.Name = request.Name.Trim();
        form.Description = Optional(request.Description);
        // Lista nueva: EF compara la columna jsonb por referencia.
        form.Fields = request.Fields.Select(f => new FormField
        {
            Key = f.Key.Trim(),
            Label = f.Label.Trim(),
            Type = f.Type,
            Required = f.Required,
            Hint = Optional(f.Hint),
            Options = f.Type == FormFieldType.Choice
                ? f.Options.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList()
                : [],
        }).ToList();
        form.Enabled = request.Enabled;
        form.UpdatedAt = now;
    }

    private static FormTemplateDto ToDto(FormTemplate form, int total, int pending) => new(
        form.Id,
        form.Name,
        form.Description,
        form.Fields.Select(f => new FormFieldDto(f.Key, f.Label, f.Type, f.Required, f.Hint, f.Options)).ToList(),
        form.Enabled,
        total,
        pending,
        form.CreatedAt,
        form.UpdatedAt);

    private static FormSubmissionDto ToDto(FormSubmission s, IReadOnlyList<ActionExecutionDto> actions) =>
        new(s.Id, s.FormTemplateId, s.Values, s.ConversationId, s.CallerNumber, s.Status, s.CreatedAt, actions);

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[a-z0-9_]{1,64}$")]
    private static partial Regex KeyPattern();
}
