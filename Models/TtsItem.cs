namespace MeetingInterpreter.Models;

public sealed class TtsItem
{
    public long SequenceNumber { get; init; }

    public SupportedLanguage TargetLanguage { get; init; }

    public string OriginalText { get; init; } = string.Empty;

    public string TranslatedText { get; init; } = string.Empty;

    public TranslationResult Result { get; init; } = new();
}
