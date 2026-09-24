namespace Mapache.Domain.Entities;

public enum CallStatus
{
    Ringing,
    InProgress,
    Completed,
    Transferred,
    Failed,
}

public class Call
{
    public Guid Id { get; set; }

    public Guid ExtensionId { get; set; }

    public Extension? Extension { get; set; }

    public required string CallerNumber { get; set; }

    public CallStatus Status { get; set; }

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
