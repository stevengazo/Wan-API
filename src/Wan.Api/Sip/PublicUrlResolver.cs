using Wan.Api.Controllers;
using Wan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Wan.Api.Sip;

/// <summary>URL pública efectiva: la de Configuración → General o, si no hay, <c>App:PublicUrl</c>.</summary>
public sealed class PublicUrlResolver(WanDbContext db, IOptions<AppOptions> options)
{
    public async Task<string?> ResolveAsync(CancellationToken ct)
    {
        var panel = await db.SiteSettings.AsNoTracking().Select(s => s.PublicUrl).SingleOrDefaultAsync(ct);
        return panel ?? SiteSettingsController.Normalize(options.Value.PublicUrl);
    }
}
