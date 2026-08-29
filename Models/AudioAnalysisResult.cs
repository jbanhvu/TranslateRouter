namespace MeetingInterpreter.Models;

public sealed class AudioAnalysisResult
{
    public int SampleRate { get; init; }

    public int Channels { get; init; }

    public int BitsPerSample { get; init; }

    public int Bytes { get; init; }

    public double DurationMs { get; init; }

    public double Rms { get; init; }

    public double Peak { get; init; }
}
