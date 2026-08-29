namespace MeetingInterpreter.Models;

public sealed class SpeechRecognitionSettings
{
    public string Model { get; set; } = "latest_long";

    public int SampleRate { get; set; } = 16000;

    public bool EnableAutomaticPunctuation { get; set; } = true;

    public int MaxAlternatives { get; set; } = 3;

    public int PreRollMs { get; set; } = 300;

    public int PostRollMs { get; set; } = 200;

    public int SilenceDurationMs { get; set; } = 900;

    public int MinimumSpeechDurationMs { get; set; } = 300;

    public int MaximumSpeechDurationMs { get; set; } = 15000;

    public TimeSpan MaxStreamingSessionDuration { get; set; } = TimeSpan.FromSeconds(290);

    public int StreamingRestartOverlapMs { get; set; } = 800;

    public int RecognitionTimeoutSeconds { get; set; } = 25;

    public int TranslationTimeoutSeconds { get; set; } = 25;

    public int TtsTimeoutSeconds { get; set; } = 25;

    public int ApiRetryCount { get; set; } = 2;

    public bool SaveRecognitionAudioForDebug { get; set; }
}
