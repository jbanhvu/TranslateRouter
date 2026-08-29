namespace MeetingInterpreter.Models;

public sealed class InterpreterResult
{
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public InterpreterEngineType Engine { get; init; }

    public SupportedLanguage SourceLanguage { get; init; }

    public SupportedLanguage TargetLanguage { get; init; }

    public string OriginalText { get; init; } = string.Empty;

    public string TranslatedText { get; init; } = string.Empty;

    public float? Confidence { get; init; }

    public double RecognitionMilliseconds { get; init; }

    public double TranslationMilliseconds { get; init; }

    public double AiProcessingMilliseconds { get; init; }

    public double SynthesisMilliseconds { get; init; }

    public double TotalMilliseconds { get; init; }

    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }
}
