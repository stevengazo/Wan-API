using System.ComponentModel.DataAnnotations;
using Mapache.Api.Auth;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Knowledge;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record KnowledgeBaseDto(Guid Id, string Name, string? Description, bool Enabled, int DocumentCount, int ChunkCount, DateTimeOffset CreatedAt);

public sealed record SaveKnowledgeBaseRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Name,
    [StringLength(1000, ErrorMessage = Messages.MaxLength)] string? Description = null,
    bool Enabled = true);

public sealed record KnowledgeDocumentDto(
    Guid Id,
    string FileName,
    long SizeBytes,
    KnowledgeDocumentStatus Status,
    string? Error,
    int ChunkCount,
    DateTimeOffset CreatedAt);

public sealed record KnowledgeSearchRequest([Required(ErrorMessage = Messages.Required)] string Query, Guid? KnowledgeBaseId = null);

[ApiController]
[Authorize]
public sealed class KnowledgeController(MapacheDbContext db, KnowledgeService knowledge, TimeProvider time) : ControllerBase
{
    [HttpGet("api/knowledge")]
    public async Task<IReadOnlyList<KnowledgeBaseDto>> List(CancellationToken ct) =>
        await db.KnowledgeBases.AsNoTracking()
            .OrderBy(b => b.Name)
            .Select(b => new KnowledgeBaseDto(
                b.Id, b.Name, b.Description, b.Enabled,
                db.KnowledgeDocuments.Count(d => d.KnowledgeBaseId == b.Id),
                db.KnowledgeChunks.Count(c => c.KnowledgeBaseId == b.Id),
                b.CreatedAt))
            .ToListAsync(ct);

    [HttpGet("api/knowledge/{id:guid}")]
    public async Task<ActionResult<KnowledgeBaseDto>> Get(Guid id, CancellationToken ct) =>
        (await List(ct)).FirstOrDefault(b => b.Id == id) is { } dto ? dto : NotFound();

    [HttpPost("api/knowledge")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<KnowledgeBaseDto>> Create(SaveKnowledgeBaseRequest request, CancellationToken ct)
    {
        var knowledgeBase = new KnowledgeBase { Name = request.Name.Trim(), CreatedAt = time.GetUtcNow() };
        Apply(knowledgeBase, request);
        db.KnowledgeBases.Add(knowledgeBase);
        await db.SaveChangesAsync(ct);
        var dto = (await List(ct)).First(b => b.Id == knowledgeBase.Id);
        return CreatedAtAction(nameof(Get), new { id = knowledgeBase.Id }, dto);
    }

    [HttpPut("api/knowledge/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<KnowledgeBaseDto>> Update(Guid id, SaveKnowledgeBaseRequest request, CancellationToken ct)
    {
        var knowledgeBase = await db.KnowledgeBases.FindAsync([id], ct);
        if (knowledgeBase is null)
        {
            return NotFound();
        }

        Apply(knowledgeBase, request);
        await db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>Borra también sus documentos y fragmentos.</summary>
    [HttpDelete("api/knowledge/{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await db.KnowledgeBases.Where(b => b.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();

    [HttpGet("api/knowledge/{id:guid}/documents")]
    public async Task<IReadOnlyList<KnowledgeDocumentDto>> Documents(Guid id, CancellationToken ct) =>
        await db.KnowledgeDocuments.AsNoTracking()
            .Where(d => d.KnowledgeBaseId == id)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new KnowledgeDocumentDto(d.Id, d.FileName, d.SizeBytes, d.Status, d.Error, d.ChunkCount, d.CreatedAt))
            .ToListAsync(ct);

    /// <summary>
    /// Sube uno o varios archivos. Cada uno se procesa en el momento; los que no se pueden leer quedan como
    /// fallidos con el motivo, sin frenar a los demás.
    /// </summary>
    [HttpPost("api/knowledge/{id:guid}/documents")]
    [Authorize(Roles = Roles.Admin)]
    [RequestSizeLimit(100_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 100_000_000)]
    public async Task<ActionResult<IReadOnlyList<KnowledgeDocumentDto>>> Upload(Guid id, [FromForm] IFormFileCollection files, CancellationToken ct)
    {
        if (!await db.KnowledgeBases.AnyAsync(b => b.Id == id, ct))
        {
            return NotFound();
        }

        if (files.Count == 0)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Elige al menos un archivo");
        }

        var result = new List<KnowledgeDocumentDto>();
        foreach (var file in files)
        {
            if (file.Length > KnowledgeService.MaxFileBytes)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"\"{file.FileName}\" supera los 20 MB");
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var d = await knowledge.AddDocumentAsync(id, file.FileName, buffer.ToArray(), ct);
            result.Add(new KnowledgeDocumentDto(d.Id, d.FileName, d.SizeBytes, d.Status, d.Error, d.ChunkCount, d.CreatedAt));
        }

        return result;
    }

    [HttpDelete("api/knowledge/documents/{documentId:guid}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteDocument(Guid documentId, CancellationToken ct) =>
        await db.KnowledgeDocuments.Where(d => d.Id == documentId).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();

    /// <summary>Prueba de búsqueda desde el panel: devuelve lo mismo que recibiría el bot.</summary>
    [HttpPost("api/knowledge/search")]
    public async Task<IReadOnlyList<KnowledgeHit>> Search(KnowledgeSearchRequest request, CancellationToken ct) =>
        await knowledge.SearchAsync(request.Query, limit: 5, baseId: request.KnowledgeBaseId, ct: ct);

    private static void Apply(KnowledgeBase knowledgeBase, SaveKnowledgeBaseRequest request)
    {
        knowledgeBase.Name = request.Name.Trim();
        knowledgeBase.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        knowledgeBase.Enabled = request.Enabled;
    }
}
