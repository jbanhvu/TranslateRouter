namespace MeetingInterpreter.Models;

public sealed class InterpreterSettings
{
    public SpeechRecognitionSettings SpeechRecognition { get; } = new();

    public GeminiSettings Gemini { get; } = new();

    public DeepgramSettings Deepgram { get; } = new();

    public InterpreterEngineType EngineType { get; set; } = InterpreterEngineType.Gemini25Pro;

    public SttEngineType SttEngineType { get; set; } = SttEngineType.GoogleSpeechToText;

    public double VadThreshold { get; set; } = 0.025;

    public int SilenceDurationMs
    {
        get => SpeechRecognition.SilenceDurationMs;
        set => SpeechRecognition.SilenceDurationMs = value;
    }

    public int MinimumSpeechDurationMs
    {
        get => SpeechRecognition.MinimumSpeechDurationMs;
        set => SpeechRecognition.MinimumSpeechDurationMs = value;
    }

    public int MaximumSpeechDurationMs
    {
        get => SpeechRecognition.MaximumSpeechDurationMs;
        set => SpeechRecognition.MaximumSpeechDurationMs = value;
    }

    public int PostPlaybackSilenceMs { get; set; } = 250;

    public double MinimumRecognitionConfidence { get; set; } = 0.50;

    public int MaxPendingUtterances { get; set; } = 5;

    public int MaxPendingTranslations { get; set; } = 10;

    public bool SuppressMicDuringHeadsetPlayback { get; set; }

    public bool RecognitionOnlyMode { get; set; }
}
