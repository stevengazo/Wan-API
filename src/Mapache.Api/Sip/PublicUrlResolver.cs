using Mapache.Api.Controllers;
using Mapache.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mapache.Api.Sip;

/// <summary>URL pública efectiva: la de Configuración → General o, si no hay, <c>App:PublicUrl</c>.</summary>
public sealed class PublicUrlResolver(MapacheDbContext db, IOptions<AppOptions> options)
{
    public async Task<string?> ResolveAsync(CancellationToken ct)
    {
        var panel = await db.SiteSettings.AsNoTracking().Select(s => s.PublicUrl).SingleOrDefaultAsync(ct);
        return panel ?? SiteSettingsController.Normalize(options.Value.PublicUrl);
    }
}
