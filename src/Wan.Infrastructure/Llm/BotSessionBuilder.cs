using Mapache.Domain.Entities;
using Mapache.Infrastructure.Campaigns;
using Mapache.Infrastructure.Knowledge;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Infrastructure.Llm;

/// <summary>Lo que el bot necesita para un turno o una llamada: el modelo de texto, el contexto y las herramientas.</summary>
public sealed record BotSession(LlmTarget Target, string? Context, IReadOnlyDictionary<string, BotTool> Tools)
{
    /// <summary>Instrucciones del panel más el contexto de Mapache, para proveedores que reciben un solo prompt.</summary>
    public string Instructions(string? fallback = null) =>
        string.Join("\n\n", new[] { Target.SystemPrompt ?? fallback, Context }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>
/// Arma la sesión del bot igual para todos los proveedores de voz: el Custom LLM (ElevenLabs, Deepgram) la
/// pide en cada turno y OpenAI Realtime una vez al empezar la llamada.
/// </summary>
public sealed class BotSessionBuilder(
    MapacheDbContext db,
    LlmResolver resolver,
    KnowledgeService knowledge,
    BotToolRegistry toolRegistry,
    TimeProvider time)
{
    /// <param name="agentTools">Herramientas que ya trae el proveedor de voz (las del agente de ElevenLabs).</param>
    /// <param name="lastUserMessage">Con qué buscar en el conocimiento; nulo = sin búsqueda previa.</param>
    public async Task<BotSession> BuildAsync(
        BotCallContext call, IReadOnlyCollection<string> agentTools, string? lastUserMessage, CancellationToken ct)
    {
        var target = await resolver.ResolveAsync(ct);
        var directory = await db.DirectoryEntries.AsNoTracking().Where(d => d.Enabled).ToListAsync(ct);
        var forms = await db.FormTemplates.AsNoTracking().Where(f => f.Enabled).ToListAsync(ct);
        var callSettings = await db.CallSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        var extension = call.ExtensionId is { } id ? await db.Extensions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        var hours = extension?.BusinessHours.StateAt(time.GetUtcNow()) ?? BusinessHoursState.Open;

        var tools = await toolRegistry.LoadAsync(call, ct);

        // Búsqueda previa con lo último que dijo quien llama: el texto completo responde en milisegundos, así
        // que no se nota en la latencia del turno.
        var hits = lastUserMessage is { Length: > 2 } question ? await knowledge.SearchAsync(question, limit: 3, ct: ct) : [];

        // En campañas el guion va primero: define para qué es la llamada.
        var contact = call.ContactId is { } contactId
            ? await db.CampaignContacts.AsNoTracking().Include(c => c.Campaign).SingleOrDefaultAsync(c => c.Id == contactId, ct)
            : null;

        var toolNames = agentTools.Concat(tools.Keys).ToHashSet(StringComparer.Ordinal);
        var context = string.Join("\n\n", new[]
            {
                contact?.Campaign is { } campaign ? CampaignPrompt.Build(campaign, contact) : null,
                BotContext.Build(toolNames, directory, forms, callSettings, hours, extension?.BusinessHours.MessageFor(hours)),
                KnowledgeService.Format(hits),
            }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

        return new BotSession(target, context.Length == 0 ? null : context, tools);
    }
}
