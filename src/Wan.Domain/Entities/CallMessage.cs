namespace Wan.Domain.Entities;

public enum CallMessageStatus
{
    New,
    Read,
    Done,
}

/// <summary>Recado que el bot toma para alguien que no pudo atender.</summary>
public class CallMessage
{
    public Guid Id { get; set; }

    /// <summary>Para quién es, tal como lo dijo quien llama.</summary>
    public required string Recipient { get; set; }

    /// <summary>Entrada del directorio que corresponde a <see cref="Recipient"/>, si se encontró.</summary>
    public Guid? DirectoryEntryId { get; set; }

    public DirectoryEntry? DirectoryEntry { get; set; }

    public string? CallerName { get; set; }

    public string? CallbackNumber { get; set; }

    public required string Body { get; set; }

    public bool Urgent { get; set; }

    public CallMessageStatus Status { get; set; } = CallMessageStatus.New;

    public string? ConversationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
