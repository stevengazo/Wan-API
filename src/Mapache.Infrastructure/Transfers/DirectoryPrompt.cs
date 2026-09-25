using System.Globalization;
using System.Text;
using Mapache.Domain.Entities;

namespace Mapache.Infrastructure.Transfers;

/// <summary>Convierte el directorio en texto para el bot y busca destinos por nombre o tema.</summary>
public static class DirectoryPrompt
{
    /// <summary>Bloque que se agrega a las instrucciones del bot; nulo si no hay entradas.</summary>
    public static string? Build(IReadOnlyCollection<DirectoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine("Directorio de transferencias. Si quien llama necesita hablar con alguno de estos destinos, o su consulta")
            .AppendLine("corresponde a lo que atiende, ofrécele transferirlo e indica el destino exacto de la lista.")
            .AppendLine("No transfieras a destinos que no estén aquí.");
        foreach (var entry in entries.OrderBy(e => e.Department).ThenBy(e => e.Name))
        {
            text.Append("- ").Append(entry.Name);
            if (!string.IsNullOrWhiteSpace(entry.Department))
            {
                text.Append(" (").Append(entry.Department).Append(')');
            }

            text.Append(": destino ").Append(entry.Target);
            if (!string.IsNullOrWhiteSpace(entry.Description))
            {
                text.Append(". Atiende: ").Append(entry.Description);
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Búsqueda tolerante (sin mayúsculas ni tildes, por palabras) sobre nombre, departamento, destino y descripción.
    /// Devuelve primero las entradas con más palabras en común.
    /// </summary>
    public static IReadOnlyList<DirectoryEntry> Search(IEnumerable<DirectoryEntry> entries, string query, int limit = 5)
    {
        var words = Words(query);
        if (words.Count == 0)
        {
            return [];
        }

        return entries
            .Select(entry =>
            {
                var name = Words(entry.Name);
                var haystack = Words($"{entry.Department} {entry.Target} {entry.Description}");
                // Coincidir en el nombre pesa más que en la descripción.
                var score = words.Sum(w => (name.Any(n => n.StartsWith(w, StringComparison.Ordinal)) ? 3 : 0)
                    + (haystack.Any(h => h.StartsWith(w, StringComparison.Ordinal)) ? 1 : 0));
                return (entry, score);
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.entry.Name)
            .Take(limit)
            .Select(x => x.entry)
            .ToList();
    }

    // Palabras frecuentes en lo que dice quien llama que no identifican un destino.
    private static readonly HashSet<string> StopWords =
    [
        "con", "que", "por", "para", "los", "las", "del", "una", "uno", "unos", "unas", "mas", "sus", "les", "hay",
        "quiero", "necesito", "hablar", "llamar", "favor", "alguien", "sobre", "tengo", "como", "esta", "este", "estoy",
    ];

    private static List<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = text.Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }
        }

        // Palabras de una o dos letras ("de", "la") y las vacías solo meten ruido; los números sí cuentan.
        return plain.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.All(char.IsDigit) || (w.Length > 2 && !StopWords.Contains(w)))
            .ToList();
    }
}
