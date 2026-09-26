namespace Mapache.Domain.Entities;

/// <summary>Conjunto de documentos que el bot consulta: "Políticas", "Catálogo de planes".</summary>
public class KnowledgeBase
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Solo las bases activas se consultan en las llamadas.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}

public enum KnowledgeDocumentStatus
{
    Ready,
    Failed,
}

/// <summary>Archivo subido a una base. Se guarda el texto extraído en fragmentos, no el archivo original.</summary>
public class KnowledgeDocument
{
    public Guid Id { get; set; }

    public Guid KnowledgeBaseId { get; set; }

    public required string FileName { get; set; }

    public long SizeBytes { get; set; }

    public KnowledgeDocumentStatus Status { get; set; }

    /// <summary>Por qué no se pudo leer, si falló.</summary>
    public string? Error { get; set; }

    public int ChunkCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Fragmento de texto indexado para búsqueda.</summary>
public class KnowledgeChunk
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public KnowledgeDocument? Document { get; set; }

    /// <summary>Copia del id de la base para filtrar sin join.</summary>
    public Guid KnowledgeBaseId { get; set; }

    public int Index { get; set; }

    public required string Text { get; set; }

    /// <summary>
    /// <see cref="Text"/> en minúsculas y sin tildes: el índice de búsqueda se arma sobre esto para que
    /// "tecnico" encuentre "técnico".
    /// </summary>
    public required string SearchText { get; set; }
}
