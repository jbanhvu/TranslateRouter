namespace MeetingInterpreter.Models;

public sealed class PlaybackItem
{
    public long SequenceNumber { get; init; }

    public SupportedLanguage TargetLanguage { get; init; }

    public byte[] AudioData { get; init; } = [];

    public string OriginalText { get; init; } = string.Empty;

    public string TranslatedText { get; init; } = string.Empty;

    public TranslationResult Result { get; init; } = new();
}
