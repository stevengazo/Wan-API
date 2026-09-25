using System.Text.Json.Nodes;

namespace Mapache.Infrastructure.Tools;

/// <summary>
/// Herramienta que ejecuta Mapache dentro del Custom LLM (no ElevenLabs): el modelo la pide, Mapache la
/// corre y le devuelve el resultado antes de que el bot siga hablando.
/// </summary>
public sealed record BotTool(
    string Name,
    string Description,
    JsonObject Parameters,
    Func<JsonObject, CancellationToken, Task<string>> ExecuteAsync)
{
    /// <summary>Definición en formato de tools de chat completions.</summary>
    public JsonObject ToDefinition() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = Parameters.DeepClone(),
        },
    };
}

/// <summary>Qué extensión atiende y qué llamada es; nulos fuera de una llamada (pruebas, panel).</summary>
public sealed record BotCallContext(Guid? ExtensionId, Guid? CallId)
{
    public static readonly BotCallContext None = new(null, null);
}

/// <summary>Origen de herramientas: conocimiento, peticiones HTTP, servidores MCP, transferencias, recados...</summary>
public interface IBotToolSource
{
    Task<IReadOnlyList<BotTool>> GetToolsAsync(BotCallContext call, CancellationToken ct);
}

/// <summary>Reúne las herramientas de todos los orígenes. La primera con un nombre gana.</summary>
public sealed class BotToolRegistry(IEnumerable<IBotToolSource> sources)
{
    public async Task<IReadOnlyDictionary<string, BotTool>> LoadAsync(BotCallContext call, CancellationToken ct)
    {
        var tools = new Dictionary<string, BotTool>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var tool in await source.GetToolsAsync(call, ct))
            {
                tools.TryAdd(tool.Name, tool);
            }
        }

        return tools;
    }

    /// <summary>
    /// Agrega las herramientas de Mapache a las que ya trae la petición de ElevenLabs. Si el agente tiene
    /// una con el mismo nombre, se respeta la del agente.
    /// </summary>
    public static IReadOnlyDictionary<string, BotTool> AddTo(JsonObject request, IReadOnlyDictionary<string, BotTool> tools)
    {
        if (request["tools"] is not JsonArray existing)
        {
            existing = [];
            request["tools"] = existing;
        }

        var taken = existing.Select(t => t?["function"]?["name"]?.GetValue<string>()).OfType<string>().ToHashSet();
        var added = tools.Where(t => !taken.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value);
        foreach (var tool in added.Values)
        {
            existing.Add(tool.ToDefinition());
        }

        if (existing.Count == 0)
        {
            request.Remove("tools");
        }

        return added;
    }
}

/// <summary>JSON Schema de parámetros a partir de una lista simple.</summary>
public static class ToolSchema
{
    public static JsonObject Object(params (string Name, string Type, string Description, bool Required)[] properties) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(properties.Select(p => KeyValuePair.Create<string, JsonNode?>(
            p.Name, new JsonObject { ["type"] = p.Type, ["description"] = p.Description }))),
        ["required"] = new JsonArray(properties.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
    };
}
