namespace MeetingInterpreter.Services;

public sealed class DigitalSilenceDetector
{
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BytesPerSample = BitsPerSample / 8;
    private const int MaximumSilentSampleMagnitude = 1;
    private const double MinimumZeroSampleRatio = 0.995;

    private readonly int _sampleRate;
    private readonly int _muteDetectionMilliseconds;
    private readonly int _resumeDetectionMilliseconds;
    private double _digitalSilenceMilliseconds;
    private double _signalMilliseconds;

    public DigitalSilenceDetector(
        int sampleRate = 16000,
        int muteDetectionMilliseconds = 350,
        int resumeDetectionMilliseconds = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(muteDetectionMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resumeDetectionMilliseconds);

        _sampleRate = sampleRate;
        _muteDetectionMilliseconds = muteDetectionMilliseconds;
        _resumeDetectionMilliseconds = resumeDetectionMilliseconds;
    }

    public bool IsPhysicallyMuted { get; private set; }

    public DigitalSilenceTransition ProcessBuffer(byte[] buffer, int bytesRecorded)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (bytesRecorded <= 0)
        {
            return DigitalSilenceTransition.None;
        }

        bytesRecorded = Math.Min(bytesRecorded, buffer.Length);
        var durationMilliseconds = bytesRecorded * 1000d
            / (_sampleRate * BytesPerSample * Channels);
        if (IsDigitalSilence(buffer, bytesRecorded))
        {
            _signalMilliseconds = 0;
            _digitalSilenceMilliseconds += durationMilliseconds;
            if (!IsPhysicallyMuted && _digitalSilenceMilliseconds >= _muteDetectionMilliseconds)
            {
                IsPhysicallyMuted = true;
                return DigitalSilenceTransition.Muted;
            }

            return DigitalSilenceTransition.None;
        }

        _digitalSilenceMilliseconds = 0;
        _signalMilliseconds += durationMilliseconds;
        if (IsPhysicallyMuted && _signalMilliseconds >= _resumeDetectionMilliseconds)
        {
            IsPhysicallyMuted = false;
            return DigitalSilenceTransition.Resumed;
        }

        return DigitalSilenceTransition.None;
    }

    public void Reset()
    {
        IsPhysicallyMuted = false;
        _digitalSilenceMilliseconds = 0;
        _signalMilliseconds = 0;
    }

    public static bool IsDigitalSilence(byte[] buffer, int bytesRecorded)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        bytesRecorded = Math.Min(bytesRecorded, buffer.Length);
        var sampleCount = bytesRecorded / BytesPerSample;
        if (sampleCount == 0)
        {
            return false;
        }

        var zeroSamples = 0;
        for (var offset = 0; offset + 1 < bytesRecorded; offset += BytesPerSample)
        {
            var sample = (short)(buffer[offset] | (buffer[offset + 1] << 8));
            if (sample == 0)
            {
                zeroSamples++;
                continue;
            }

            if (Math.Abs((int)sample) > MaximumSilentSampleMagnitude)
            {
                return false;
            }
        }

        return zeroSamples / (double)sampleCount >= MinimumZeroSampleRatio;
    }
}

public enum DigitalSilenceTransition
{
    None,
    Muted,
    Resumed
}
