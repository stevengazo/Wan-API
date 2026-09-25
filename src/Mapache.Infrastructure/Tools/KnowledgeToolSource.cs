using Mapache.Infrastructure.Knowledge;
using Mapache.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Infrastructure.Tools;

/// <summary>
/// <c>search_knowledge_base</c>: además del contexto que se agrega en cada turno, el bot puede buscar con
/// sus propias palabras cuando necesita un dato puntual.
/// </summary>
public sealed class KnowledgeToolSource(MapacheDbContext db, KnowledgeService knowledge) : IBotToolSource
{
    public const string Name = "search_knowledge_base";

    public async Task<IReadOnlyList<BotTool>> GetToolsAsync(CancellationToken ct)
    {
        if (!await db.KnowledgeChunks.AnyAsync(c => db.KnowledgeBases.Any(b => b.Id == c.KnowledgeBaseId && b.Enabled), ct))
        {
            return [];
        }

        return
        [
            new BotTool(
                Name,
                "Busca en la base de conocimiento de la empresa (políticas, precios, horarios, procedimientos). " +
                "Úsala antes de responder algo que no sepas con certeza.",
                ToolSchema.Object(("query", "string", "Qué buscar, en pocas palabras", true)),
                async (args, token) =>
                {
                    var query = args["query"]?.GetValue<string>() ?? "";
                    var hits = await knowledge.SearchAsync(query, limit: 4, ct: token);
                    return KnowledgeService.Format(hits) ?? "No se encontró información sobre eso en la base de conocimiento.";
                }),
        ];
    }
}
