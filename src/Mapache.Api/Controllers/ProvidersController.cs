using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record ProviderDto(Guid Id, string Name, ProviderType Type, string SipServer);

[ApiController]
[Route("api/providers")]
[Authorize]
public sealed class ProvidersController(MapacheDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ProviderDto>> List(CancellationToken ct) =>
        await db.Providers.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new ProviderDto(p.Id, p.Name, p.Type, p.SipServer))
            .ToListAsync(ct);
}
