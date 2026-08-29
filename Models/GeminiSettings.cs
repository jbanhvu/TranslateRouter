namespace MeetingInterpreter.Models;

public sealed class GeminiSettings
{
    public string ApiKey { get; set; } = string.Empty;

    public string ApiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";

    public string Model { get; set; } = "gemini-3.1-flash-live-preview";

    public string TtsModel { get; set; } = "gemini-2.5-flash-preview-tts";

    public string TtsVoice { get; set; } = "Kore";

    public double Temperature { get; set; }
}
