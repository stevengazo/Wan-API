namespace Mapache.Domain.Entities;

public enum CallStatus
{
    Ringing,
    InProgress,
    Completed,
    Transferred,
    Failed,

    /// <summary>Nadie contestó (colgaron mientras sonaba o se rechazó por estar ocupada).</summary>
    Missed,
}

public enum CallDirection
{
    Inbound,
    Outbound,
}

public class Call
{
    public Guid Id { get; set; }

    public Guid ExtensionId { get; set; }

    public Extension? Extension { get; set; }

    public required string CallerNumber { get; set; }

    public CallStatus Status { get; set; }

    public CallDirection Direction { get; set; }

    /// <summary>Por qué terminó: "el llamante colgó", "transferida a 101", el error...</summary>
    public string? EndReason { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    public string? ElevenLabsConversationId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public List<CallTranscript> Transcript { get; set; } = [];
}

public enum TranscriptSpeaker
{
    Caller,
    Agent,
}

public class CallTranscript
{
    public Guid Id { get; set; }

    public Guid CallId { get; set; }

    public TranscriptSpeaker Speaker { get; set; }

    public required string Text { get; set; }

    public DateTimeOffset At { get; set; }
}
