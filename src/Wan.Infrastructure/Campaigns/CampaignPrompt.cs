using System.Text;
using System.Text.RegularExpressions;
using Wan.Domain.Entities;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;

namespace Wan.Infrastructure.Campaigns;

/// <summary>Instrucciones del bot en una llamada de campaña y reemplazo de las variables del contacto.</summary>
public static partial class CampaignPrompt
{
    public const string ResultTool = "campaign_result";

    /// <summary>Variables disponibles: las del contacto más <c>nombre</c> y <c>telefono</c>.</summary>
    public static Dictionary<string, string> Variables(CampaignContact contact)
    {
        var variables = new Dictionary<string, string>(contact.Variables, StringComparer.OrdinalIgnoreCase)
        {
            ["telefono"] = contact.PhoneNumber,
        };
        if (!string.IsNullOrWhiteSpace(contact.Name))
        {
            variables["nombre"] = contact.Name;
        }

        return variables;
    }

    /// <summary>Cada <c>{{variable}}</c> por su valor; las que no existen quedan vacías.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> variables) =>
        Placeholder().Replace(template, m => variables.TryGetValue(m.Groups[1].Value, out var value) ? value : "");

    public static string Build(Campaign campaign, CampaignContact contact)
    {
        var variables = Variables(contact);
        var who = string.IsNullOrWhiteSpace(contact.Name) ? "la persona" : contact.Name;
        var text = new StringBuilder()
            .AppendLine($"Esta es una llamada SALIENTE: tú llamas a {who} ({contact.PhoneNumber}) de parte de la empresa, en la campaña «{campaign.Name}».")
            .AppendLine("Objetivo e indicaciones:")
            .AppendLine(Render(campaign.Instructions, variables).Trim());

        var extra = contact.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Value)).ToList();
        if (extra.Count > 0)
        {
            text.AppendLine("Datos del contacto: " + string.Join("; ", extra.Select(v => $"{v.Key}: {v.Value}")));
        }

        text.AppendLine("Sé breve y respetuoso. Si la persona no puede o no quiere hablar, agradece y despídete. " +
            "Si pide que no la llamen más, confírmalo y regístralo.");
        text.Append($"Antes de despedirte registra el resultado con la herramienta {ResultTool}.");
        return text.ToString();
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_áéíóúñ]+)\s*\}\}")]
    private static partial Regex Placeholder();
}

/// <summary>
/// <c>campaign_result</c>: el bot registra cómo terminó la conversación con el contacto (uno de los resultados de
/// la campaña) y notas breves. Solo existe en llamadas de campaña.
/// </summary>
public sealed class CampaignToolSource(WanDbContext db, TimeProvider time) : IBotToolSource
{
    public const string DoNotCall = "No volver a llamar";

    public async Task<IReadOnlyList<BotTool>> GetToolsAsync(BotCallContext call, CancellationToken ct)
    {
        if (call.ContactId is not { } contactId)
        {
            return [];
        }

        var campaign = await db.CampaignContacts.AsNoTracking().Where(c => c.Id == contactId).Select(c => c.Campaign).SingleOrDefaultAsync(ct);
        if (campaign is null)
        {
            return [];
        }

        var outcomes = campaign.Outcomes.Append(DoNotCall).Distinct().ToList();
        var parameters = ToolSchema.Object(
            ("outcome", "string", "Resultado de la llamada: " + string.Join(", ", outcomes), true),
            ("notes", "string", "Notas breves: qué dijo, qué quedó pendiente", false));
        parameters["properties"]!["outcome"]!["enum"] = new System.Text.Json.Nodes.JsonArray(outcomes.Select(o => (System.Text.Json.Nodes.JsonNode)o).ToArray());

        return
        [
            new BotTool(
                CampaignPrompt.ResultTool,
                "Registra el resultado de la llamada de la campaña. Úsala una vez, antes de despedirte.",
                parameters,
                async (args, token) =>
                {
                    var outcome = args["outcome"]?.GetValue<string>()?.Trim();
                    var chosen = outcomes.FirstOrDefault(o => string.Equals(o, outcome, StringComparison.OrdinalIgnoreCase));
                    if (chosen is null)
                    {
                        return $"Resultado desconocido. Usa uno de: {string.Join(", ", outcomes)}.";
                    }

                    var contact = await db.CampaignContacts.SingleAsync(c => c.Id == contactId, token);
                    contact.Outcome = chosen;
                    var notes = args["notes"]?.GetValue<string>()?.Trim();
                    contact.Notes = string.IsNullOrEmpty(notes) ? null : notes.Length > 2000 ? notes[..2000] : notes;
                    contact.UpdatedAt = time.GetUtcNow();
                    await db.SaveChangesAsync(token);
                    return "Resultado registrado. Despídete cordialmente.";
                }),
        ];
    }
}
