using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Infrastructure.Tools;

/// <summary>Arma y ejecuta las peticiones HTTP configuradas en el panel.</summary>
public sealed partial class HttpToolRunner(HttpClient http, ISecretProtector protector)
{
    /// <summary>Lo que se le devuelve al bot se recorta: las respuestas largas gastan contexto y tiempo.</summary>
    public const int MaxResponseChars = 4000;

    // Sin escapar tildes ni eñes: el bot lee el texto tal cual y cada \u00F1 gasta contexto.
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<string> ExecuteAsync(HttpTool tool, JsonObject arguments, CancellationToken ct)
    {
        var url = RenderUrl(tool.UrlTemplate, arguments);
        using var request = new HttpRequestMessage(new HttpMethod(tool.Method), url);
        if (!string.IsNullOrWhiteSpace(tool.BodyTemplate) && tool.Method is not ("GET" or "DELETE"))
        {
            request.Content = new StringContent(RenderBody(tool.BodyTemplate, arguments), Encoding.UTF8, "application/json");
        }

        foreach (var (name, value) in Headers(tool))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(tool.TimeoutSeconds, 1, 30)));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return Describe((int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Error: el sistema externo no respondió a tiempo. Dile a quien llama que no pudiste consultarlo ahora.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: no se pudo conectar con el sistema externo ({ex.Message}).";
        }
    }

    public Dictionary<string, string> Headers(HttpTool tool) =>
        tool.HeadersEncrypted is null
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(tool.HeadersEncrypted)) ?? [];

    /// <summary>Cada <c>{{nombre}}</c> de la URL se reemplaza por el valor codificado para URL; los que faltan quedan vacíos.</summary>
    public static string RenderUrl(string template, JsonObject arguments) =>
        Placeholder().Replace(template, m => Uri.EscapeDataString(Scalar(arguments[m.Groups[1].Value])));

    /// <summary>Cada <c>{{nombre}}</c> del cuerpo se reemplaza por el valor en JSON: texto con comillas, números y booleanos sin ellas.</summary>
    public static string RenderBody(string template, JsonObject arguments) =>
        Placeholder().Replace(template, m => arguments[m.Groups[1].Value]?.ToJsonString() ?? "null");

    /// <summary>Estado y cuerpo para el bot; si es JSON se compacta.</summary>
    public static string Describe(int status, string body)
    {
        var text = body;
        try
        {
            text = JsonNode.Parse(body)?.ToJsonString(Compact) ?? body;
        }
        catch (JsonException)
        {
            // No es JSON: se devuelve como texto.
        }

        if (text.Length > MaxResponseChars)
        {
            text = text[..MaxResponseChars] + "… (respuesta recortada)";
        }

        return status is >= 200 and < 300
            ? text
            : $"Error: el sistema externo respondió {status}. {text}";
    }

    private static string Scalar(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex Placeholder();
}

/// <summary>Herramientas HTTP activas del panel.</summary>
public sealed class HttpToolSource(MapacheDbContext db, HttpToolRunner runner) : IBotToolSource
{
    public async Task<IReadOnlyList<BotTool>> GetToolsAsync(CancellationToken ct)
    {
        var tools = await db.HttpTools.AsNoTracking().Where(t => t.Enabled).ToListAsync(ct);
        return tools.Select(tool => new BotTool(
            tool.Name,
            tool.Description,
            Schema(tool),
            (args, token) => runner.ExecuteAsync(tool, args, token))).ToList();
    }

    public static JsonObject Schema(HttpTool tool) => ToolSchema.Object(tool.Parameters
        .Select(p => (p.Name, p.Type switch
        {
            HttpToolParameterType.Number => "number",
            HttpToolParameterType.Boolean => "boolean",
            _ => "string",
        }, p.Description ?? p.Name, p.Required))
        .ToArray());
}
