using System.Text.Json.Serialization;

namespace MeetingInterpreter.Models;

public sealed class GeminiStructuredResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; } = true;

    [JsonPropertyName("detectedLanguage")]
    public string DetectedLanguage { get; init; } = string.Empty;

    [JsonPropertyName("originalText")]
    public string OriginalText { get; init; } = string.Empty;

    [JsonPropertyName("targetLanguage")]
    public string TargetLanguage { get; init; } = string.Empty;

    [JsonPropertyName("translatedText")]
    public string TranslatedText { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
