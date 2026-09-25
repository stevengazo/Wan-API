namespace Mapache.Domain.Entities;

/// <summary>
/// Token para que un cliente MCP externo (Claude Desktop, Cursor, otro agente) use el servidor MCP de Mapache.
/// Solo se guarda su hash: el valor se muestra una vez al crearlo.
/// </summary>
public class McpAccessToken
{
    public Guid Id { get; set; }

    /// <summary>Para reconocerlo en el panel: "Claude Desktop de Ana".</summary>
    public required string Name { get; set; }

    /// <summary>SHA-256 del token, en hexadecimal.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Primeros caracteres del token, para identificarlo sin revelarlo.</summary>
    public required string Prefix { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
