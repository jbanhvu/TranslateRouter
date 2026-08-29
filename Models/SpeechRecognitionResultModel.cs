namespace MeetingInterpreter.Models;

public sealed class SpeechRecognitionResultModel
{
    public string Text { get; init; } = string.Empty;

    public SupportedLanguage Language { get; init; }

    public string RawLanguageCode { get; init; } = string.Empty;

    public float? Confidence { get; init; }

    public IReadOnlyList<SpeechAlternativeModel> Alternatives { get; init; } = Array.Empty<SpeechAlternativeModel>();
}
