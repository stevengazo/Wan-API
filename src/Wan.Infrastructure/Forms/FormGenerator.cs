using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wan.Domain.Entities;
using Wan.Infrastructure.Llm;

namespace Wan.Infrastructure.Forms;

/// <summary>Borrador de formulario para revisar en el panel antes de guardarlo.</summary>
public sealed record FormDraft(string Name, string? Description, IReadOnlyList<FormField> Fields);

/// <summary>Arma un formulario a partir de una descripción en lenguaje natural con el proveedor de IA activo.</summary>
public sealed class FormGenerator(LlmJsonClient llm)
{
    public const int MaxFields = 25;

    private const string System = """
        Diseñas formularios que un asistente telefónico con IA completa durante una llamada, preguntándole los datos a quien llama.
        Devuelve el formulario en el esquema pedido, en español.
        - name: corto y claro ("Solicitud de cotización").
        - description: cuándo debe usarlo el asistente, en una o dos frases.
        - fields: solo los datos necesarios, en el orden natural de una conversación. Entre 2 y 12 campos salvo que el pedido exija más.
        - key: snake_case sin tildes, única (nombre_cliente).
        - type: Text, Number, Phone, Email, Date, YesNo o Choice. Choice solo con opciones cerradas, listadas en options; en los demás tipos options va vacío.
        - required: true solo para lo imprescindible.
        - hint: indicación breve para el asistente sobre cómo pedir o validar el dato, o vacío.
        No incluyas campos que el sistema ya conoce, como el número desde el que llaman, salvo que el pedido lo mencione.
        """;

    public static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "description", "fields"],
          "properties": {
            "name": { "type": "string" },
            "description": { "type": "string" },
            "fields": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["key", "label", "type", "required", "hint", "options"],
                "properties": {
                  "key": { "type": "string" },
                  "label": { "type": "string" },
                  "type": { "type": "string", "enum": ["Text", "Number", "Phone", "Email", "Date", "YesNo", "Choice"] },
                  "required": { "type": "boolean" },
                  "hint": { "type": "string" },
                  "options": { "type": "array", "items": { "type": "string" } }
                }
              }
            }
          }
        }
        """)!.AsObject();

    public async Task<FormDraft> GenerateAsync(string request, CancellationToken ct)
    {
        var json = await llm.CompleteAsync(System, request.Trim(), "form_template", Schema, ct);
        return Sanitize(json);
    }

    /// <summary>
    /// Deja el borrador válido para guardar aunque el modelo se salga del pedido: claves normalizadas y
    /// únicas, tipos conocidos, opciones solo donde corresponden y un máximo de campos.
    /// </summary>
    public static FormDraft Sanitize(JsonElement json)
    {
        var name = Text(json, "name") is { Length: > 0 } n ? Truncate(n, 128) : "Formulario";
        var description = Text(json, "description") is { Length: > 0 } d ? Truncate(d, 1000) : null;

        var fields = new List<FormField>();
        var keys = new HashSet<string>();
        if (json.TryGetProperty("fields", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray().Take(MaxFields))
            {
                var label = Text(item, "label");
                if (string.IsNullOrEmpty(label))
                {
                    continue;
                }

                var key = UniqueKey(ToKey(Text(item, "key") is { Length: > 0 } k ? k : label), keys);
                var type = Enum.TryParse<FormFieldType>(Text(item, "type"), ignoreCase: true, out var t) ? t : FormFieldType.Text;
                var options = item.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                    ? o.EnumerateArray().Select(x => x.GetString()?.Trim()).OfType<string>().Where(x => x.Length > 0).Distinct().ToList()
                    : [];
                if (type == FormFieldType.Choice && options.Count == 0)
                {
                    type = FormFieldType.Text;
                }

                fields.Add(new FormField
                {
                    Key = key,
                    Label = Truncate(label, 128),
                    Type = type,
                    Required = item.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True,
                    Hint = Text(item, "hint") is { Length: > 0 } h ? Truncate(h, 256) : null,
                    Options = type == FormFieldType.Choice ? options : [],
                });
            }
        }

        return new FormDraft(name, description, fields);
    }

    /// <summary>"Nombre del cliente" → "nombre_del_cliente", igual que el editor del panel.</summary>
    public static string ToKey(string text)
    {
        var plain = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            plain.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        }

        var key = string.Join('_', plain.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        return key.Length == 0 ? "campo" : Truncate(key, 60);
    }

    private static string UniqueKey(string key, HashSet<string> used)
    {
        var candidate = key;
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = $"{key}_{i}";
        }

        return candidate;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
