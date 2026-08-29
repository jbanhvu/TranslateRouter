namespace MeetingInterpreter.Models;

public sealed class TranslationResult
{
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public InterpreterEngineType Engine { get; set; }

    public SupportedLanguage SourceLanguage { get; set; }

    public SupportedLanguage TargetLanguage { get; set; }

    public string OriginalText { get; set; } = string.Empty;

    public string TranslatedText { get; set; } = string.Empty;

    public float? Confidence { get; set; }

    public double RecognitionMilliseconds { get; set; }

    public double TranslationMilliseconds { get; set; }

    public double AiProcessingMilliseconds { get; set; }

    public double SynthesisMilliseconds { get; set; }

    public double PlaybackPreparationMilliseconds { get; set; }

    public double TotalMilliseconds { get; set; }

    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }
}
