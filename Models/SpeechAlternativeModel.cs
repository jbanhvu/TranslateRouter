namespace MeetingInterpreter.Models;

public sealed class SpeechAlternativeModel
{
    public string Text { get; init; } = string.Empty;

    public float? Confidence { get; init; }
}
