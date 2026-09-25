using System.Text;
using System.Text.RegularExpressions;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Mapache.Infrastructure.Knowledge;

public sealed record KnowledgeHit(string Text, string DocumentName, string BaseName, float Rank);

/// <summary>Indexa documentos en fragmentos y busca con el texto completo de Postgres en español.</summary>
public sealed class KnowledgeService(MapacheDbContext db, TimeProvider time)
{
    public const string SearchVectorColumn = "SearchVector";
    public const int MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>Extrae el texto y lo guarda en fragmentos. Si el archivo no se puede leer, queda como fallido con el motivo.</summary>
    public async Task<KnowledgeDocument> AddDocumentAsync(Guid baseId, string fileName, byte[] content, CancellationToken ct)
    {
        var document = new KnowledgeDocument
        {
            Id = Guid.CreateVersion7(),
            KnowledgeBaseId = baseId,
            FileName = Path.GetFileName(fileName),
            SizeBytes = content.LongLength,
            CreatedAt = time.GetUtcNow(),
        };

        try
        {
            var chunks = DocumentText.Chunk(DocumentText.Extract(fileName, content));
            if (chunks.Count == 0)
            {
                throw new DocumentTextException("El archivo no tiene texto. Si es un PDF escaneado, primero pásalo por OCR.");
            }

            db.KnowledgeChunks.AddRange(chunks.Select((text, index) => new KnowledgeChunk
            {
                DocumentId = document.Id,
                KnowledgeBaseId = baseId,
                Index = index,
                Text = text,
                SearchText = DocumentText.Normalize(text),
            }));
            document.Status = KnowledgeDocumentStatus.Ready;
            document.ChunkCount = chunks.Count;
        }
        catch (DocumentTextException ex)
        {
            document.Status = KnowledgeDocumentStatus.Failed;
            document.Error = ex.Message;
        }

        db.KnowledgeDocuments.Add(document);
        await db.SaveChangesAsync(ct);
        return document;
    }

    /// <summary>
    /// Fragmentos más relevantes en las bases activas (o en <paramref name="baseId"/>). Las palabras de la
    /// consulta se combinan con OR: quien llama habla en frases, no en palabras clave.
    /// </summary>
    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int limit = 5, Guid? baseId = null, CancellationToken ct = default)
    {
        if (ToTsQuery(query) is not { } tsQuery)
        {
            return [];
        }

        var chunks = db.KnowledgeChunks.AsNoTracking()
            .Where(c => baseId == null
                ? db.KnowledgeBases.Any(b => b.Id == c.KnowledgeBaseId && b.Enabled)
                : c.KnowledgeBaseId == baseId);

        // Ordenar por un record construido en el Select no se traduce: se ordena por un anónimo y se mapea después.
        var rows = await chunks
            .Where(c => EF.Property<NpgsqlTsVector>(c, SearchVectorColumn).Matches(EF.Functions.ToTsQuery("spanish", tsQuery)))
            .Select(c => new
            {
                c.Text,
                c.Document!.FileName,
                BaseName = db.KnowledgeBases.Where(b => b.Id == c.KnowledgeBaseId).Select(b => b.Name).First(),
                Rank = EF.Property<NpgsqlTsVector>(c, SearchVectorColumn).RankCoverDensity(EF.Functions.ToTsQuery("spanish", tsQuery)),
            })
            .OrderByDescending(x => x.Rank)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(x => new KnowledgeHit(x.Text, x.FileName, x.BaseName, x.Rank)).ToList();
    }

    /// <summary>Texto para el bot con los fragmentos encontrados, dentro de un presupuesto de caracteres.</summary>
    public static string? Format(IReadOnlyList<KnowledgeHit> hits, int maxChars = 3000)
    {
        if (hits.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder("Información de la base de conocimiento que puede servir para responder. ")
            .AppendLine("Úsala solo si es pertinente y no inventes lo que no esté aquí.");
        foreach (var hit in hits)
        {
            var entry = $"\n[{hit.BaseName} · {hit.DocumentName}]\n{hit.Text}\n";
            if (text.Length + entry.Length > maxChars)
            {
                break;
            }

            text.Append(entry);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>"¿Cuánto cuesta el plan Pro?" → "cuanto | cuesta | plan | pro"; nulo si no queda ninguna palabra.</summary>
    public static string? ToTsQuery(string query)
    {
        var words = Regex.Split(DocumentText.Normalize(query), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length > 1)
            .Distinct()
            .Take(20)
            .ToList();
        return words.Count == 0 ? null : string.Join(" | ", words);
    }
}
