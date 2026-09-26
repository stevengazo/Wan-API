using System.Text;
using Wan.Api.Auth;
using Wan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Wan.Api.Controllers;

public sealed record AuditEventDto(
    Guid Id, DateTimeOffset At, Guid? UserId, string? UserEmail, string Action, string? Target, string? TargetId, string? IpAddress, string? Details);

/// <summary>Consulta del registro de auditoría. Solo lectura: no hay forma de editar ni borrar eventos.</summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
public sealed class AuditController(WanDbContext db, TimeProvider time) : ControllerBase
{
    private const int MaxRows = 2000;

    /// <param name="action">Código exacto o prefijo con punto ("recording." = todo lo de grabaciones).</param>
    /// <param name="search">Busca en usuario, objeto y detalle.</param>
    /// <param name="format">"csv" descarga el resultado para archivarlo o entregarlo a un auditor.</param>
    [HttpGet("api/audit")]
    public async Task<IActionResult> List(
        [FromQuery] int days = 30, [FromQuery] string? action = null, [FromQuery] string? search = null, [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        var from = time.GetUtcNow().AddDays(-Math.Clamp(days, 1, 3650));
        var query = db.AuditEvents.AsNoTracking().Where(e => e.At >= from);
        if (!string.IsNullOrWhiteSpace(action))
        {
            query = action.EndsWith('.') ? query.Where(e => e.Action.StartsWith(action)) : query.Where(e => e.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(e =>
                EF.Functions.ILike(e.UserEmail ?? "", term) || EF.Functions.ILike(e.Target ?? "", term) || EF.Functions.ILike(e.Details ?? "", term)
                || EF.Functions.ILike(e.IpAddress ?? "", term));
        }

        var rows = await query.OrderByDescending(e => e.At).Take(MaxRows)
            .Select(e => new AuditEventDto(e.Id, e.At, e.UserId, e.UserEmail, e.Action, e.Target, e.TargetId, e.IpAddress, e.Details))
            .ToListAsync(ct);

        if (format != "csv")
        {
            return Ok(rows);
        }

        var csv = new StringBuilder("fecha;usuario;accion;objeto;id_objeto;ip;detalle\n");
        foreach (var r in rows)
        {
            csv.AppendJoin(';', new[] { r.At.ToString("O"), r.UserEmail, r.Action, r.Target, r.TargetId, r.IpAddress, r.Details }.Select(Cell)).Append('\n');
        }

        // BOM: Excel abre el archivo con tildes correctas.
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv", $"auditoria-{time.GetUtcNow():yyyyMMdd}.csv");
    }

    private static string Cell(string? value) =>
        value is null ? "" : value.Contains(';') || value.Contains('"') || value.Contains('\n') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
