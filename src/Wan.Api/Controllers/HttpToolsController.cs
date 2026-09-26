using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wan.Api.Auth;
using Wan.Domain.Entities;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Security;
using Wan.Infrastructure.Tools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Wan.Api.Controllers;

public sealed record HttpToolParameterDto(string Name, HttpToolParameterType Type, string? Description, bool Required);

/// <summary>Los valores de los headers no salen de la API: solo sus nombres.</summary>
public sealed record HttpToolDto(
    Guid Id,
    string Name,
    string Description,
    string Method,
    string UrlTemplate,
    string? BodyTemplate,
    IReadOnlyList<string> HeaderNames,
    IReadOnlyList<HttpToolParameterDto> Parameters,
    int TimeoutSeconds,
    bool Enabled);

/// <summary><c>Headers</c> nulo conserva los guardados; un objeto (aunque vacío) los reemplaza.</summary>
public sealed record SaveHttpToolRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(64, ErrorMessage = Messages.MaxLength)] string Name,
    [Required(ErrorMessage = Messages.Required), StringLength(1000, ErrorMessage = Messages.MaxLength)] string Description,
    [Required(ErrorMessage = Messages.Required)] string Method,
    [Required(ErrorMessage = Messages.Required), StringLength(2000, ErrorMessage = Messages.MaxLength)] string UrlTemplate,
    string? BodyTemplate,
    Dictionary<string, string>? Headers,
    IReadOnlyList<HttpToolParameterDto> Parameters,
    [Range(1, 30, ErrorMessage = Messages.Range)] int TimeoutSeconds = 10,
    bool Enabled = true);

public sealed record TestHttpToolRequest(JsonObject Arguments);

[ApiController]
[Authorize(Roles = Roles.Admin)]
public sealed partial class HttpToolsController(
    WanDbContext db,
    ISecretProtector protector,
    HttpToolRunner runner,
    TimeProvider time) : ControllerBase
{
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    [HttpGet("api/http-tools")]
    public async Task<IReadOnlyList<HttpToolDto>> List(CancellationToken ct) =>
        (await db.HttpTools.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct)).Select(ToDto).ToList();

    [HttpPost("api/http-tools")]
    public async Task<ActionResult<HttpToolDto>> Create(SaveHttpToolRequest request, CancellationToken ct)
    {
        if (await ValidateAsync(null, request, ct) is { } error)
        {
            return error;
        }

        var tool = new HttpTool { Name = "", Description = "", UrlTemplate = "", CreatedAt = time.GetUtcNow() };
        Apply(tool, request);
        db.HttpTools.Add(tool);
        await db.SaveChangesAsync(ct);
        return ToDto(tool);
    }

    [HttpPut("api/http-tools/{id:guid}")]
    public async Task<ActionResult<HttpToolDto>> Update(Guid id, SaveHttpToolRequest request, CancellationToken ct)
    {
        var tool = await db.HttpTools.FindAsync([id], ct);
        if (tool is null)
        {
            return NotFound();
        }

        if (await ValidateAsync(id, request, ct) is { } error)
        {
            return error;
        }

        Apply(tool, request);
        await db.SaveChangesAsync(ct);
        return ToDto(tool);
    }

    [HttpDelete("api/http-tools/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await db.HttpTools.Where(t => t.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();

    /// <summary>Ejecuta la herramienta con los valores de prueba y devuelve exactamente lo que recibiría el bot.</summary>
    [HttpPost("api/http-tools/{id:guid}/test")]
    public async Task<ActionResult<object>> Test(Guid id, TestHttpToolRequest request, CancellationToken ct)
    {
        var tool = await db.HttpTools.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
        if (tool is null)
        {
            return NotFound();
        }

        var result = await runner.ExecuteAsync(tool, request.Arguments, ct);
        return new { url = HttpToolRunner.RenderUrl(tool.UrlTemplate, request.Arguments), result };
    }

    private async Task<ActionResult?> ValidateAsync(Guid? id, SaveHttpToolRequest request, CancellationToken ct)
    {
        if (!NamePattern().IsMatch(request.Name.Trim()))
        {
            ModelState.AddModelError(nameof(SaveHttpToolRequest.Name), "Solo minúsculas, números y guion bajo; empieza con letra");
        }
        else if (await db.HttpTools.AnyAsync(t => t.Name == request.Name.Trim() && t.Id != id, ct))
        {
            ModelState.AddModelError(nameof(SaveHttpToolRequest.Name), "Ya existe una herramienta con ese nombre");
        }

        if (!Methods.Contains(request.Method.ToUpperInvariant()))
        {
            ModelState.AddModelError(nameof(SaveHttpToolRequest.Method), "Método no soportado");
        }

        var sampleUrl = HttpToolRunner.RenderUrl(request.UrlTemplate.Trim(), []);
        if (!Uri.TryCreate(sampleUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            ModelState.AddModelError(nameof(SaveHttpToolRequest.UrlTemplate), "Debe ser una URL http(s)");
        }

        if (!string.IsNullOrWhiteSpace(request.BodyTemplate))
        {
            // Con cada marcador reemplazado por un valor cualquiera, el cuerpo tiene que ser JSON válido.
            try
            {
                JsonNode.Parse(HttpToolRunner.RenderBody(request.BodyTemplate, request.Parameters.ToJsonObject()));
            }
            catch (JsonException)
            {
                ModelState.AddModelError(nameof(SaveHttpToolRequest.BodyTemplate), "El cuerpo no es JSON válido (los {{marcadores}} van sin comillas)");
            }
        }

        foreach (var (parameter, index) in request.Parameters.Select((p, i) => (p, i)))
        {
            if (!ParameterPattern().IsMatch(parameter.Name))
            {
                ModelState.AddModelError($"{nameof(SaveHttpToolRequest.Parameters)}[{index}].{nameof(HttpToolParameterDto.Name)}", "Solo letras, números y guion bajo");
            }
        }

        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }

    private void Apply(HttpTool tool, SaveHttpToolRequest request)
    {
        tool.Name = request.Name.Trim();
        tool.Description = request.Description.Trim();
        tool.Method = request.Method.ToUpperInvariant();
        tool.UrlTemplate = request.UrlTemplate.Trim();
        tool.BodyTemplate = string.IsNullOrWhiteSpace(request.BodyTemplate) ? null : request.BodyTemplate.Trim();
        tool.Parameters = request.Parameters
            .Select(p => new HttpToolParameter { Name = p.Name.Trim(), Type = p.Type, Description = p.Description?.Trim(), Required = p.Required })
            .ToList();
        tool.TimeoutSeconds = request.TimeoutSeconds;
        tool.Enabled = request.Enabled;
        if (request.Headers is not null)
        {
            var headers = request.Headers.Where(h => !string.IsNullOrWhiteSpace(h.Key)).ToDictionary(h => h.Key.Trim(), h => h.Value);
            tool.HeadersEncrypted = headers.Count == 0 ? null : protector.Protect(JsonSerializer.Serialize(headers));
        }
    }

    private HttpToolDto ToDto(HttpTool t) => new(
        t.Id, t.Name, t.Description, t.Method, t.UrlTemplate, t.BodyTemplate,
        runner.Headers(t).Keys.ToList(),
        t.Parameters.Select(p => new HttpToolParameterDto(p.Name, p.Type, p.Description, p.Required)).ToList(),
        t.TimeoutSeconds, t.Enabled);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex ParameterPattern();
}

internal static class HttpToolParameterSamples
{
    /// <summary>Un valor de ejemplo por parámetro para validar que el cuerpo queda como JSON.</summary>
    public static JsonObject ToJsonObject(this IReadOnlyList<HttpToolParameterDto> parameters) =>
        new(parameters.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Name, p.Type switch
        {
            HttpToolParameterType.Number => JsonValue.Create(1),
            HttpToolParameterType.Boolean => JsonValue.Create(true),
            _ => JsonValue.Create("x"),
        })));
}
