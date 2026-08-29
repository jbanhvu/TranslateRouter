namespace MeetingInterpreter.Models;

public sealed class UtteranceLifecycle
{
    public long SequenceNumber { get; init; }

    public UtteranceStage Stage { get; set; }

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public DateTime? QueuedForTranslationAt { get; set; }

    public DateTime? ProcessingStartedAt { get; set; }

    public string LastError { get; set; } = string.Empty;
}
