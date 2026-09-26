using Wan.Api.Audit;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Wan.Api.Auth;
using Wan.Domain.Entities;
using Wan.Infrastructure.Actions;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Wan.Api.Controllers;

/// <summary>El secreto del webhook nunca sale de la API; solo se informa si está cargado.</summary>
public sealed record FormActionDto(
    Guid Id,
    Guid FormTemplateId,
    FormActionType Type,
    string Name,
    string Target,
    bool HasSecret,
    bool Enabled,
    string? Method,
    string? BodyTemplate,
    IReadOnlyList<string> HeaderNames);

/// <summary>
/// <c>Secret</c> nulo o vacío conserva el guardado; <c>ClearSecret</c> lo borra. <c>Headers</c> nulo conserva
/// los guardados; un objeto (aunque vacío) los reemplaza.
/// </summary>
public sealed record SaveFormActionRequest(
    FormActionType Type,
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Name,
    [Required(ErrorMessage = Messages.Required), StringLength(2000, ErrorMessage = Messages.MaxLength)] string Target,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? Secret = null,
    bool ClearSecret = false,
    bool Enabled = true,
    [RegularExpression("^(GET|POST|PUT|PATCH|DELETE)$", ErrorMessage = "Método no soportado")] string? Method = null,
    [StringLength(8000, ErrorMessage = Messages.MaxLength)] string? BodyTemplate = null,
    Dictionary<string, string>? Headers = null);

public sealed record SmtpSettingsDto(string? Host, int Port, bool UseTls, string? Username, bool HasPassword, string? From);

/// <summary><c>Password</c> vacía conserva la guardada.</summary>
public sealed record SaveSmtpSettingsRequest(
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? Host,
    [Range(1, 65535, ErrorMessage = Messages.Range)] int Port,
    bool UseTls,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? Username,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? Password,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? From);

[ApiController]
[Authorize(Roles = Roles.Admin)]
public sealed partial class FormActionsController(
    WanDbContext db,
    ISecretProtector protector,
    FormActionSender sender,
    IFormActionQueue queue,
    TimeProvider time,
    AuditLog audit) : ControllerBase
{
    [HttpGet("api/forms/{formId:guid}/actions")]
    public async Task<IReadOnlyList<FormActionDto>> List(Guid formId, CancellationToken ct) =>
        (await db.FormActions.AsNoTracking().Where(a => a.FormTemplateId == formId).OrderBy(a => a.CreatedAt).ToListAsync(ct))
        .Select(ToDto)
        .ToList();

    [HttpPost("api/forms/{formId:guid}/actions")]
    public async Task<ActionResult<FormActionDto>> Create(Guid formId, SaveFormActionRequest request, CancellationToken ct)
    {
        if (!await db.FormTemplates.AnyAsync(f => f.Id == formId, ct))
        {
            return NotFound();
        }

        if (Validate(request) is { } error)
        {
            return error;
        }

        var action = new FormAction { FormTemplateId = formId, Name = "", Target = "", CreatedAt = time.GetUtcNow() };
        Apply(action, request);
        db.FormActions.Add(action);
        await db.SaveChangesAsync(ct);
        return ToDto(action);
    }

    [HttpPut("api/form-actions/{id:guid}")]
    public async Task<ActionResult<FormActionDto>> Update(Guid id, SaveFormActionRequest request, CancellationToken ct)
    {
        var action = await db.FormActions.FindAsync([id], ct);
        if (action is null)
        {
            return NotFound();
        }

        if (Validate(request) is { } error)
        {
            return error;
        }

        Apply(action, request);
        await db.SaveChangesAsync(ct);
        return ToDto(action);
    }

    [HttpDelete("api/form-actions/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await db.FormActions.Where(a => a.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();

    /// <summary>Envía una respuesta de ejemplo en el momento, sin cola, para verificar la configuración.</summary>
    [HttpPost("api/form-actions/{id:guid}/test")]
    public async Task<ActionResult<ToolResult>> Test(Guid id, CancellationToken ct)
    {
        var action = await db.FormActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        var form = action is null ? null : await db.FormTemplates.AsNoTracking().SingleAsync(f => f.Id == action.FormTemplateId, ct);
        if (action is null || form is null)
        {
            return NotFound();
        }

        var sample = new FormSubmission
        {
            Id = Guid.Empty,
            FormTemplateId = form.Id,
            Values = form.Fields.ToDictionary(f => f.Key, Sample),
            CallerNumber = "+50600000000",
            ConversationId = "prueba",
            CreatedAt = time.GetUtcNow(),
        };

        try
        {
            await sender.SendAsync(action, form, sample, ct);
            return new ToolResult(true, "Enviado. Revisa que haya llegado.");
        }
        catch (FormActionException ex)
        {
            return new ToolResult(false, ex.Message);
        }
    }

    [HttpPost("api/action-executions/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        if (await db.ActionExecutions.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ActionExecutionStatus.Pending), ct) == 0)
        {
            return NotFound();
        }

        queue.Enqueue(id);
        return Accepted();
    }

    [HttpGet("api/settings/smtp")]
    public async Task<SmtpSettingsDto> GetSmtp(CancellationToken ct) =>
        ToDto(await db.SmtpSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new SmtpSettings());

    [HttpPut("api/settings/smtp")]
    public async Task<ActionResult<SmtpSettingsDto>> SaveSmtp(SaveSmtpSettingsRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.From) && !MimeKit.MailboxAddress.TryParse(request.From, out _))
        {
            ModelState.AddModelError(nameof(SaveSmtpSettingsRequest.From), "Remitente inválido");
            return ValidationProblem(ModelState);
        }

        var smtp = await db.SmtpSettings.SingleOrDefaultAsync(ct);
        if (smtp is null)
        {
            smtp = new SmtpSettings();
            db.SmtpSettings.Add(smtp);
        }

        smtp.Host = Optional(request.Host);
        smtp.Port = request.Port;
        smtp.UseTls = request.UseTls;
        smtp.Username = Optional(request.Username);
        smtp.From = Optional(request.From);
        if (!string.IsNullOrEmpty(request.Password))
        {
            smtp.PasswordEncrypted = protector.Protect(request.Password);
        }
        else if (smtp.Username is null)
        {
            smtp.PasswordEncrypted = null;
        }

        smtp.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.SettingsChanged, "Correo saliente", details: smtp.Host);
        return ToDto(smtp);
    }

    private ActionResult? Validate(SaveFormActionRequest request)
    {
        var target = request.Target.Trim();
        if (request.Type == FormActionType.Email)
        {
            var invalid = FormActionSender.SplitRecipients(target).Where(r => !MimeKit.MailboxAddress.TryParse(r, out _)).ToList();
            if (invalid.Count > 0 || !FormActionSender.SplitRecipients(target).Any())
            {
                ModelState.AddModelError(nameof(SaveFormActionRequest.Target), "Escribe correos válidos separados por coma");
            }
        }
        // En peticiones HTTP la URL puede llevar marcadores ({{id}}): se valida con valores de relleno.
        else if (!Uri.TryCreate(request.Type == FormActionType.HttpRequest ? PlaceholderPattern().Replace(target, "x") : target, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http"))
        {
            ModelState.AddModelError(nameof(SaveFormActionRequest.Target), "Debe ser una URL http(s)");
        }

        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }

    private void Apply(FormAction action, SaveFormActionRequest request)
    {
        action.Type = request.Type;
        action.Name = request.Name.Trim();
        action.Target = request.Target.Trim();
        action.Enabled = request.Enabled;
        var isHttp = request.Type == FormActionType.HttpRequest;
        action.Method = isHttp ? request.Method ?? "POST" : null;
        action.BodyTemplate = isHttp && !string.IsNullOrWhiteSpace(request.BodyTemplate) ? request.BodyTemplate.Trim() : null;
        if (!isHttp)
        {
            action.HeadersEncrypted = null;
        }
        else if (request.Headers is not null)
        {
            var headers = request.Headers.Where(h => !string.IsNullOrWhiteSpace(h.Key)).ToDictionary(h => h.Key.Trim(), h => h.Value);
            action.HeadersEncrypted = headers.Count == 0 ? null : protector.Protect(JsonSerializer.Serialize(headers));
        }

        if (request.Type is not (FormActionType.Webhook or FormActionType.HttpRequest) || request.ClearSecret)
        {
            action.SecretEncrypted = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.Secret))
        {
            action.SecretEncrypted = protector.Protect(request.Secret.Trim());
        }
    }

    private static string Sample(FormField field) => field.Type switch
    {
        FormFieldType.Number => "10",
        FormFieldType.Phone => "+50688881234",
        FormFieldType.Email => "cliente@ejemplo.com",
        FormFieldType.Date => "15 de octubre",
        FormFieldType.YesNo => "Sí",
        FormFieldType.Choice => field.Options.FirstOrDefault() ?? "Opción",
        _ => $"Ejemplo de {field.Label.ToLowerInvariant()}",
    };

    private FormActionDto ToDto(FormAction a) =>
        new(a.Id, a.FormTemplateId, a.Type, a.Name, a.Target, a.SecretEncrypted is not null, a.Enabled,
            a.Method, a.BodyTemplate, sender.Headers(a).Keys.ToList());

    [System.Text.RegularExpressions.GeneratedRegex(@"\{\{\s*[A-Za-z0-9_]+\s*\}\}")]
    private static partial System.Text.RegularExpressions.Regex PlaceholderPattern();

    private static SmtpSettingsDto ToDto(SmtpSettings s) =>
        new(s.Host, s.Port, s.UseTls, s.Username, s.PasswordEncrypted is not null, s.From);

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
