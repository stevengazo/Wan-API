namespace Mapache.Domain.Entities;

/// <summary>Ajustes generales del sitio. Hay una sola fila.</summary>
public class SiteSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// URL con la que se llega al panel desde internet (https://mapache.miempresa.com). Vacío = la de
    /// <c>App:PublicUrl</c> del entorno.
    /// </summary>
    public string? PublicUrl { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
