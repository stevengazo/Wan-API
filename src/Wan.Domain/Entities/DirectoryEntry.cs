namespace Wan.Domain.Entities;

/// <summary>Destino al que el bot puede transferir una llamada: una persona, un área o un número externo.</summary>
public class DirectoryEntry
{
    public Guid Id { get; set; }

    /// <summary>Cómo lo nombra quien llama: "Ventas", "Ana Rodríguez".</summary>
    public required string Name { get; set; }

    public string? Department { get; set; }

    /// <summary>Extensión, número telefónico o URI SIP al que se transfiere.</summary>
    public required string Target { get; set; }

    /// <summary>Cuándo transferir aquí, en lenguaje natural: le da contexto al bot para decidir.</summary>
    public string? Description { get; set; }

    /// <summary>Solo las entradas activas llegan al bot.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
