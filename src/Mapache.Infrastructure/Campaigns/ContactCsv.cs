using System.Globalization;
using System.Text;

namespace Mapache.Infrastructure.Campaigns;

/// <summary>Contacto leído de un CSV: número, nombre y el resto de las columnas como variables del guion.</summary>
public sealed record ImportedContact(string PhoneNumber, string? Name, Dictionary<string, string> Variables);

public sealed record ContactCsvResult(IReadOnlyList<ImportedContact> Contacts, IReadOnlyList<string> Problems);

/// <summary>
/// Lee la lista de contactos de una campaña. La primera fila son los nombres de columna; una columna de teléfono
/// es obligatoria (telefono, teléfono, phone, numero, número, celular). Acepta coma o punto y coma (Excel en
/// español exporta con punto y coma) y valores entre comillas.
/// </summary>
public static class ContactCsv
{
    private static readonly string[] PhoneColumns = ["telefono", "phone", "numero", "celular", "movil", "tel"];
    private static readonly string[] NameColumns = ["nombre", "name", "cliente", "contacto"];

    public const int MaxContacts = 10_000;

    public static ContactCsvResult Parse(string csv)
    {
        var lines = csv.Replace("\r\n", "\n").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0)
        {
            return new([], ["El archivo está vacío."]);
        }

        var separator = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
        var headers = SplitLine(lines[0], separator).Select(h => Normalize(h)).ToList();
        var phoneIndex = headers.FindIndex(h => PhoneColumns.Contains(h));
        if (phoneIndex < 0)
        {
            return new([], ["Falta la columna de teléfono (telefono, phone, numero o celular)."]);
        }

        var nameIndex = headers.FindIndex(h => NameColumns.Contains(h));
        var contacts = new List<ImportedContact>();
        var problems = new List<string>();
        var seen = new HashSet<string>();

        for (var row = 1; row < lines.Count; row++)
        {
            if (contacts.Count == MaxContacts)
            {
                problems.Add($"Se importaron los primeros {MaxContacts} contactos; el resto se ignoró.");
                break;
            }

            var cells = SplitLine(lines[row], separator);
            var phone = CleanPhone(Cell(cells, phoneIndex));
            if (phone is null)
            {
                problems.Add($"Fila {row + 1}: teléfono vacío o inválido.");
                continue;
            }

            if (!seen.Add(phone))
            {
                problems.Add($"Fila {row + 1}: {phone} está repetido.");
                continue;
            }

            var variables = new Dictionary<string, string>();
            for (var i = 0; i < headers.Count; i++)
            {
                if (i != phoneIndex && i != nameIndex && headers[i].Length > 0 && Cell(cells, i) is { Length: > 0 } value)
                {
                    variables[headers[i]] = value;
                }
            }

            contacts.Add(new ImportedContact(phone, nameIndex >= 0 ? Cell(cells, nameIndex) : null, variables));
        }

        return new(contacts, problems);
    }

    /// <summary>Solo dígitos y un + inicial; al menos 3 dígitos (extensiones internas) y como mucho 20.</summary>
    public static string? CleanPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length is < 3 or > 20)
        {
            return null;
        }

        return trimmed.StartsWith('+') ? "+" + digits : digits;
    }

    private static string? Cell(IReadOnlyList<string> cells, int index) =>
        index < cells.Count && cells[index].Trim() is { Length: > 0 } value ? value : null;

    /// <summary>Nombre de columna comparable: minúsculas, sin tildes ni espacios ("Teléfono móvil" → "telefono_movil").</summary>
    private static string Normalize(string header)
    {
        var decomposed = header.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var text = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            text.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return text.ToString().Trim('_');
    }

    private static List<string> SplitLine(string line, char separator)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == separator && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}
