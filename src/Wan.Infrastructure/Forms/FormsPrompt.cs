using System.Globalization;
using System.Net.Mail;
using System.Text;
using Wan.Domain.Entities;

namespace Wan.Infrastructure.Forms;

/// <summary>Resultado de validar lo que envió el bot: valores normalizados o qué falta corregir.</summary>
public sealed record FormValidation(Dictionary<string, string> Values, IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>Texto de los formularios para el bot y validación de lo que envía con <c>submit_form</c>.</summary>
public static class FormsPrompt
{
    public static string? Build(IReadOnlyCollection<FormTemplate> forms)
    {
        if (forms.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine("Formularios. Cuando la conversación trate de alguno, pide los datos de a uno, con naturalidad,")
            .AppendLine("confirma los importantes y envíalo con la herramienta submit_form (form = nombre exacto, values = campo: valor).")
            .AppendLine("No inventes datos: si quien llama no sabe uno opcional, omítelo.");
        foreach (var form in forms.OrderBy(f => f.Name))
        {
            text.AppendLine().Append("Formulario \"").Append(form.Name).Append('"');
            if (!string.IsNullOrWhiteSpace(form.Description))
            {
                text.Append(": ").Append(form.Description);
            }

            text.AppendLine();
            foreach (var field in form.Fields)
            {
                text.Append("- ").Append(field.Key).Append(" (").Append(field.Label).Append(", ").Append(Describe(field))
                    .Append(field.Required ? ", obligatorio" : ", opcional").Append(')');
                if (!string.IsNullOrWhiteSpace(field.Hint))
                {
                    text.Append(": ").Append(field.Hint);
                }

                text.AppendLine();
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Busca el formulario por id o por nombre, sin mayúsculas ni tildes.</summary>
    public static FormTemplate? Find(IEnumerable<FormTemplate> forms, string nameOrId)
    {
        var list = forms.ToList();
        if (Guid.TryParse(nameOrId, out var id))
        {
            return list.FirstOrDefault(f => f.Id == id);
        }

        var wanted = Normalize(nameOrId);
        return list.FirstOrDefault(f => Normalize(f.Name) == wanted);
    }

    /// <summary>
    /// Normaliza cada valor según su tipo y lista lo que falta o no es válido, en frases que el bot pueda
    /// usar para volver a preguntar.
    /// </summary>
    public static FormValidation Validate(FormTemplate form, IReadOnlyDictionary<string, string?> values)
    {
        var result = new Dictionary<string, string>();
        var problems = new List<string>();

        foreach (var field in form.Fields)
        {
            var raw = values.FirstOrDefault(v => string.Equals(v.Key, field.Key, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                if (field.Required)
                {
                    problems.Add($"Falta \"{field.Label}\" ({field.Key}).");
                }

                continue;
            }

            if (Normalize(field, raw) is { } normalized)
            {
                result[field.Key] = normalized;
            }
            else
            {
                problems.Add($"\"{field.Label}\" ({field.Key}) no es válido: se esperaba {Describe(field)}.");
            }
        }

        return new FormValidation(result, problems);
    }

    private static string? Normalize(FormField field, string raw) => field.Type switch
    {
        FormFieldType.Number => decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
            ? n.ToString(CultureInfo.InvariantCulture)
            : null,
        FormFieldType.Phone => raw.Count(char.IsDigit) >= 6
            ? new string(raw.Where(c => char.IsDigit(c) || c == '+').ToArray())
            : null,
        FormFieldType.Email => MailAddress.TryCreate(raw, out var mail) ? mail.Address.ToLowerInvariant() : null,
        FormFieldType.YesNo => Normalize(raw) switch
        {
            "si" or "yes" or "true" or "verdadero" => "Sí",
            "no" or "false" or "falso" => "No",
            _ => null,
        },
        FormFieldType.Choice => field.Options.FirstOrDefault(o => Normalize(o) == Normalize(raw)),
        // Las fechas se dicen de muchas formas ("el martes", "15 de octubre"): se guardan como las dio el bot.
        _ => raw,
    };

    private static string Describe(FormField field) => field.Type switch
    {
        FormFieldType.Number => "número",
        FormFieldType.Phone => "teléfono",
        FormFieldType.Email => "correo electrónico",
        FormFieldType.Date => "fecha",
        FormFieldType.YesNo => "sí o no",
        FormFieldType.Choice => $"una de: {string.Join(", ", field.Options)}",
        _ => "texto",
    };

    private static string Normalize(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .ToLowerInvariant();
    }
}
