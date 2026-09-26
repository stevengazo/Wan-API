namespace Mapache.Domain.Entities;

/// <summary>Herramienta que publica un servidor MCP, tal como se leyó en la última sincronización.</summary>
public class McpToolInfo
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>JSON Schema de los argumentos, como texto.</summary>
    public required string InputSchema { get; set; }

    /// <summary>Si el bot la puede usar. Permite dejar fuera herramientas peligrosas del servidor.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Servidor MCP externo (HTTP) cuyas herramientas usa el bot durante la llamada.</summary>
public class McpServerConnection
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Endpoint del servidor MCP (Streamable HTTP o SSE).</summary>
    public required string Url { get; set; }

    /// <summary>Headers (p. ej. Authorization) serializados y cifrados.</summary>
    public string? HeadersEncrypted { get; set; }

    public bool Enabled { get; set; } = true;

    public List<McpToolInfo> Tools { get; set; } = [];

    /// <summary>Error de la última conexión, si falló.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
