namespace Wan.Domain.Entities;

public enum HttpToolParameterType
{
    String,
    Number,
    Boolean,
}

/// <summary>Dato que el bot debe completar para llamar a la herramienta. Se guarda en el JSON de <see cref="HttpTool.Parameters"/>.</summary>
public class HttpToolParameter
{
    /// <summary>Nombre del parámetro y del marcador <c>{{nombre}}</c> en la URL o el cuerpo.</summary>
    public required string Name { get; set; }

    public HttpToolParameterType Type { get; set; } = HttpToolParameterType.String;

    /// <summary>Qué es y cómo obtenerlo, para el bot.</summary>
    public string? Description { get; set; }

    public bool Required { get; set; } = true;
}

/// <summary>
/// Petición a un sistema externo que el bot puede hacer durante la llamada para consultar o registrar datos
/// (estado de un pedido, saldo, disponibilidad). Wan la ejecuta y le devuelve la respuesta al bot.
/// </summary>
public class HttpTool
{
    public Guid Id { get; set; }

    /// <summary>Nombre de la herramienta para el modelo, en snake_case: <c>consultar_pedido</c>.</summary>
    public required string Name { get; set; }

    /// <summary>Cuándo usarla y qué devuelve: es lo que el modelo lee para decidir.</summary>
    public required string Description { get; set; }

    public string Method { get; set; } = "GET";

    /// <summary>URL con marcadores: <c>https://erp/api/pedidos/{{numero}}</c>. Los valores se codifican para URL.</summary>
    public required string UrlTemplate { get; set; }

    /// <summary>Cuerpo JSON con marcadores; cada <c>{{nombre}}</c> se reemplaza por el valor en JSON.</summary>
    public string? BodyTemplate { get; set; }

    /// <summary>Headers (p. ej. Authorization) serializados y cifrados: suelen llevar credenciales.</summary>
    public string? HeadersEncrypted { get; set; }

    public List<HttpToolParameter> Parameters { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 10;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
