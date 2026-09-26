using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wan.Domain.Entities;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Security;
using Wan.Infrastructure.Tools;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace Wan.Infrastructure.Actions;

/// <summary>El destino rechazó o no respondió: el mensaje se guarda en el log de la ejecución.</summary>
public sealed class FormActionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Envía una respuesta de formulario por el canal de la acción. Lanza si falla, para reintentar.</summary>
public sealed class FormActionSender(HttpClient http, WanDbContext db, ISecretProtector protector)
{
    public async Task SendAsync(FormAction action, FormTemplate form, FormSubmission submission, CancellationToken ct)
    {
        switch (action.Type)
        {
            case FormActionType.Webhook:
                var body = FormActionPayloads.Webhook(form, submission).ToJsonString();
                var secret = action.SecretEncrypted is null ? null : protector.Unprotect(action.SecretEncrypted);
                await PostAsync(action.Target, body, secret, ct);
                break;
            case FormActionType.Teams:
                await PostAsync(action.Target, FormActionPayloads.Teams(form, submission).ToJsonString(), null, ct);
                break;
            case FormActionType.GoogleChat:
                await PostAsync(action.Target, FormActionPayloads.ChatText(form, submission).ToJsonString(), null, ct);
                break;
            case FormActionType.Slack:
                await PostAsync(action.Target, FormActionPayloads.ChatText(form, submission).ToJsonString(), null, ct);
                break;
            case FormActionType.Email:
                await EmailAsync(action.Target, form, submission, ct);
                break;
            case FormActionType.HttpRequest:
                await HttpRequestAsync(action, form, submission, ct);
                break;
            default:
                throw new FormActionException($"Tipo de acción desconocido: {action.Type}");
        }
    }

    /// <summary>Petición a otro sistema con los datos mapeados por marcadores; mismos reintentos que los webhooks.</summary>
    private async Task HttpRequestAsync(FormAction action, FormTemplate form, FormSubmission submission, CancellationToken ct)
    {
        var arguments = FormActionPayloads.TemplateArguments(form, submission);
        var url = HttpToolRunner.RenderUrl(action.Target, arguments);
        var method = new HttpMethod(string.IsNullOrWhiteSpace(action.Method) ? "POST" : action.Method);
        string? body = null;
        if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            body = string.IsNullOrWhiteSpace(action.BodyTemplate)
                ? FormActionPayloads.Webhook(form, submission).ToJsonString()
                : HttpToolRunner.RenderBody(action.BodyTemplate, arguments);
        }

        var secret = action.SecretEncrypted is null ? null : protector.Unprotect(action.SecretEncrypted);
        await SendHttpAsync(method, url, body, Headers(action), secret, ct);
    }

    public Dictionary<string, string> Headers(FormAction action) =>
        action.HeadersEncrypted is null
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(action.HeadersEncrypted)) ?? [];

    private Task PostAsync(string url, string body, string? secret, CancellationToken ct) =>
        SendHttpAsync(HttpMethod.Post, url, body, new Dictionary<string, string>(), secret, ct);

    private async Task SendHttpAsync(
        HttpMethod method, string url, string? body, IReadOnlyDictionary<string, string> headers, string? secret, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.Add(FormActionPayloads.EventHeader, FormActionPayloads.SubmittedEvent);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (!string.IsNullOrEmpty(secret))
        {
            request.Headers.Add(FormActionPayloads.SignatureHeader, FormActionPayloads.Sign(secret, body ?? ""));
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new FormActionException($"No se pudo conectar: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new FormActionException("El destino no respondió a tiempo", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new FormActionException($"Respondió {(int)response.StatusCode}: {Truncate(detail, 300)}");
            }
        }
    }

    private async Task EmailAsync(string recipients, FormTemplate form, FormSubmission submission, CancellationToken ct)
    {
        var smtp = await db.SmtpSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        if (smtp is not { Host: { Length: > 0 } host, From: { Length: > 0 } from })
        {
            throw new FormActionException("El correo saliente (SMTP) no está configurado");
        }

        var (subject, html, text) = FormActionPayloads.Email(form, submission);
        var message = new MimeMessage { Subject = subject };
        message.From.Add(MailboxAddress.Parse(from));
        foreach (var to in SplitRecipients(recipients))
        {
            message.To.Add(MailboxAddress.Parse(to));
        }

        message.Body = new BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();

        using var client = new SmtpClient();
        try
        {
            // 465 es TLS directo; en los demás puertos, STARTTLS si se pidió cifrado.
            var security = !smtp.UseTls ? SecureSocketOptions.None
                : smtp.Port == 465 ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
            await client.ConnectAsync(host, smtp.Port, security, ct);
            if (!string.IsNullOrEmpty(smtp.Username))
            {
                var password = smtp.PasswordEncrypted is null ? "" : protector.Unprotect(smtp.PasswordEncrypted);
                await client.AuthenticateAsync(smtp.Username, password, ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FormActionException($"SMTP: {ex.Message}", ex);
        }
    }

    public static IEnumerable<string> SplitRecipients(string recipients) =>
        recipients.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
