namespace MeetingInterpreter.Models;

public sealed class AudioUtterance
{
    public long SequenceNumber { get; init; }

    public DateTime CapturedAt { get; init; }

    public byte[] AudioData { get; init; } = [];

    public TimeSpan Duration { get; init; }
}
