namespace MeetingInterpreter.Models;

public sealed class SavedAppSettings
{
    public string GoogleCredentialPath { get; set; } = string.Empty;

    public string GeminiApiKey { get; set; } = string.Empty;

    public string DisplayLanguage { get; set; } = MeetingInterpreter.DisplayLanguage.Vietnamese.ToString();

    public string EngineType { get; set; } = InterpreterEngineType.Gemini25Pro.ToString();

    public string SpeechRecognitionModel { get; set; } = "latest_long";

    public string SttEngineType { get; set; } = MeetingInterpreter.Models.SttEngineType.GoogleSpeechToText.ToString();

    public string DeepgramApiKey { get; set; } = string.Empty;

    public string DeepgramLanguage { get; set; } = "multi";

    public string InputDeviceId { get; set; } = string.Empty;

    public string InputDeviceName { get; set; } = string.Empty;

    public string Output1DeviceId { get; set; } = string.Empty;

    public string Output1DeviceName { get; set; } = string.Empty;

    public string Output2DeviceId { get; set; } = string.Empty;

    public string Output2DeviceName { get; set; } = string.Empty;

    public double VadThreshold { get; set; } = 0.025;

    public int SilenceDurationMs { get; set; } = 900;

    public bool SuppressMicDuringHeadsetPlayback { get; set; }

    public bool RecognitionOnlyMode { get; set; }

    public bool SaveRecognitionAudioForDebug { get; set; }
}
