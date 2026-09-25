using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mapache.Domain.Entities;

namespace Mapache.Infrastructure.Actions;

/// <summary>Cuerpos de cada tipo de acción a partir de una respuesta de formulario.</summary>
public static class FormActionPayloads
{
    public const string SignatureHeader = "X-Mapache-Signature";
    public const string EventHeader = "X-Mapache-Event";
    public const string SubmittedEvent = "form.submitted";

    /// <summary>Etiqueta y valor en el orden de los campos; los que ya no existen en la plantilla van al final.</summary>
    public static IReadOnlyList<(string Key, string Label, string Value)> Fields(FormTemplate form, FormSubmission submission)
    {
        var known = form.Fields
            .Where(f => submission.Values.ContainsKey(f.Key))
            .Select(f => (f.Key, f.Label, submission.Values[f.Key]));
        var extra = submission.Values
            .Where(v => form.Fields.All(f => f.Key != v.Key))
            .Select(v => (v.Key, v.Key, v.Value));
        return known.Concat(extra).ToList();
    }

    public static string Title(FormTemplate form) => $"Nueva respuesta: {form.Name}";

    /// <summary>JSON estable para integraciones propias: incluye claves y etiquetas.</summary>
    public static JsonObject Webhook(FormTemplate form, FormSubmission submission) => new()
    {
        ["event"] = SubmittedEvent,
        ["form"] = new JsonObject { ["id"] = form.Id, ["name"] = form.Name },
        ["submission"] = new JsonObject
        {
            ["id"] = submission.Id,
            ["created_at"] = submission.CreatedAt,
            ["caller_number"] = submission.CallerNumber,
            ["conversation_id"] = submission.ConversationId,
            ["values"] = new JsonObject(submission.Values.Select(v => KeyValuePair.Create<string, JsonNode?>(v.Key, v.Value))),
            ["fields"] = new JsonArray(Fields(form, submission)
                .Select(f => (JsonNode)new JsonObject { ["key"] = f.Key, ["label"] = f.Label, ["value"] = f.Value })
                .ToArray()),
        },
    };

    /// <summary><c>sha256=&lt;hex&gt;</c> del HMAC del cuerpo: el receptor lo recalcula con el mismo secreto.</summary>
    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    /// <summary>Tarjeta adaptable con los datos como FactSet; es lo que acepta el webhook de flujos de Teams.</summary>
    public static JsonObject Teams(FormTemplate form, FormSubmission submission)
    {
        var body = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = Title(form), ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
            new JsonObject
            {
                ["type"] = "FactSet",
                ["facts"] = new JsonArray(Fields(form, submission)
                    .Select(f => (JsonNode)new JsonObject { ["title"] = f.Label, ["value"] = f.Value })
                    .ToArray()),
            },
        };
        if (Context(submission) is { } context)
        {
            body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = context, ["isSubtle"] = true, ["size"] = "Small", ["wrap"] = true });
        }

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["contentType"] = "application/vnd.microsoft.card.adaptive",
                ["content"] = new JsonObject
                {
                    ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                    ["type"] = "AdaptiveCard",
                    ["version"] = "1.4",
                    ["body"] = body,
                },
            }),
        };
    }

    /// <summary>Google Chat y Slack aceptan texto con negritas entre asteriscos.</summary>
    public static JsonObject ChatText(FormTemplate form, FormSubmission submission)
    {
        var text = new StringBuilder();
        text.Append('*').Append(Title(form)).AppendLine("*");
        foreach (var (_, label, value) in Fields(form, submission))
        {
            text.Append("• *").Append(label).Append(":* ").AppendLine(value);
        }

        if (Context(submission) is { } context)
        {
            text.Append('_').Append(context).Append('_');
        }

        return new JsonObject { ["text"] = text.ToString().TrimEnd() };
    }

    public static (string Subject, string Html, string Text) Email(FormTemplate form, FormSubmission submission)
    {
        var fields = Fields(form, submission);
        var context = Context(submission);

        var html = new StringBuilder();
        html.Append("<h2 style=\"font-family:sans-serif\">").Append(WebUtility.HtmlEncode(Title(form))).Append("</h2>")
            .Append("<table style=\"font-family:sans-serif;border-collapse:collapse\">");
        foreach (var (_, label, value) in fields)
        {
            html.Append("<tr><td style=\"padding:4px 12px 4px 0;color:#64748b\">").Append(WebUtility.HtmlEncode(label))
                .Append("</td><td style=\"padding:4px 0\">").Append(WebUtility.HtmlEncode(value)).Append("</td></tr>");
        }

        html.Append("</table>");
        if (context is not null)
        {
            html.Append("<p style=\"font-family:sans-serif;color:#64748b;font-size:12px\">").Append(WebUtility.HtmlEncode(context)).Append("</p>");
        }

        var text = string.Join('\n', fields.Select(f => $"{f.Label}: {f.Value}")) + (context is null ? "" : $"\n\n{context}");
        return (Title(form), html.ToString(), text);
    }

    private static string? Context(FormSubmission submission)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(submission.CallerNumber))
        {
            parts.Add($"Llamada de {submission.CallerNumber}");
        }

        if (!string.IsNullOrWhiteSpace(submission.ConversationId))
        {
            parts.Add($"conversación {submission.ConversationId}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
