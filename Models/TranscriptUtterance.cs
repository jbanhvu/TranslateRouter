namespace MeetingInterpreter.Models;

public sealed class TranscriptUtterance
{
    public long SequenceNumber { get; init; }

    public DateTime CreatedAt { get; init; }

    public string Text { get; init; } = string.Empty;

    public SupportedLanguage SourceLanguage { get; init; }

    public SupportedLanguage TargetLanguage { get; init; }

    public float? Confidence { get; init; }

    public double RecognitionMilliseconds { get; init; }

    public double QueueWaitMilliseconds { get; init; }
}
