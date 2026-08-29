using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class VoiceActivityDetector
{
    private const int SampleRate = 16000;
    private const int BytesPerSample = 2;

    private readonly Queue<byte[]> _preRollBuffers = new();
    private readonly List<byte> _speechBuffer = new();
    private readonly object _syncRoot = new();
    private bool _isSpeechActive;
    private int _speechDurationMs;
    private int _silenceDurationMs;
    private int _preRollBytes;

    public VoiceActivityDetector(InterpreterSettings settings)
    {
        Settings = settings;
        _preRollBytes = MillisecondsToBytes(Settings.SpeechRecognition.PreRollMs);
    }

    public InterpreterSettings Settings { get; }

    public event EventHandler? SpeechStarted;

    public event EventHandler? SpeechEnded;

    public VoiceActivityResult ProcessBuffer(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded <= 0)
        {
            return VoiceActivityResult.None(0);
        }

        var rms = CalculateRms(buffer, bytesRecorded);
        var bufferDurationMs = BytesToMilliseconds(bytesRecorded);
        byte[]? utterance = null;
        var started = false;
        var ended = false;

        lock (_syncRoot)
        {
            StorePreRoll(buffer, bytesRecorded);
            _preRollBytes = MillisecondsToBytes(Settings.SpeechRecognition.PreRollMs);

            if (!_isSpeechActive && rms > Settings.VadThreshold)
            {
                _isSpeechActive = true;
                _speechDurationMs = 0;
                _silenceDurationMs = 0;
                _speechBuffer.Clear();

                foreach (var preRollBuffer in _preRollBuffers)
                {
                    _speechBuffer.AddRange(preRollBuffer);
                }

                started = true;
            }

            if (_isSpeechActive)
            {
                _speechBuffer.AddRange(buffer.Take(bytesRecorded));
                _speechDurationMs += bufferDurationMs;

                if (rms <= Settings.VadThreshold)
                {
                    _silenceDurationMs += bufferDurationMs;
                }
                else
                {
                    _silenceDurationMs = 0;
                }

                var reachedSilenceEnd = _silenceDurationMs >= Settings.SilenceDurationMs;
                var reachedMaximum = _speechDurationMs >= Settings.MaximumSpeechDurationMs;

                if (reachedSilenceEnd || reachedMaximum)
                {
                    ended = true;
                    if (_speechDurationMs >= Settings.MinimumSpeechDurationMs)
                    {
                        if (reachedSilenceEnd)
                        {
                            TrimTrailingSilenceToPostRoll();
                        }

                        utterance = _speechBuffer.ToArray();
                    }

                    ResetSpeechOnly();
                }
            }
        }

        if (started)
        {
            SpeechStarted?.Invoke(this, EventArgs.Empty);
        }

        if (ended)
        {
            SpeechEnded?.Invoke(this, EventArgs.Empty);
        }

        return new VoiceActivityResult(rms, started, ended, utterance);
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _preRollBuffers.Clear();
            ResetSpeechOnly();
        }
    }

    public static double CalculateRms(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < BytesPerSample)
        {
            return 0;
        }

        double sumSquares = 0;
        var samples = bytesRecorded / BytesPerSample;

        for (var i = 0; i < samples * BytesPerSample; i += BytesPerSample)
        {
            var sample = BitConverter.ToInt16(buffer, i) / 32768.0;
            sumSquares += sample * sample;
        }

        return Math.Sqrt(sumSquares / samples);
    }

    private void StorePreRoll(byte[] buffer, int bytesRecorded)
    {
        var copy = new byte[bytesRecorded];
        Buffer.BlockCopy(buffer, 0, copy, 0, bytesRecorded);
        _preRollBuffers.Enqueue(copy);

        var totalBytes = _preRollBuffers.Sum(item => item.Length);
        while (totalBytes > _preRollBytes && _preRollBuffers.Count > 0)
        {
            totalBytes -= _preRollBuffers.Dequeue().Length;
        }
    }

    private void ResetSpeechOnly()
    {
        _isSpeechActive = false;
        _speechDurationMs = 0;
        _silenceDurationMs = 0;
        _speechBuffer.Clear();
    }

    private static int BytesToMilliseconds(int bytes)
        => (int)Math.Round(bytes / (double)(SampleRate * BytesPerSample) * 1000);

    private static int MillisecondsToBytes(int milliseconds)
        => SampleRate * BytesPerSample * milliseconds / 1000;

    private void TrimTrailingSilenceToPostRoll()
    {
        var extraSilenceMs = Math.Max(0, Settings.SilenceDurationMs - Settings.SpeechRecognition.PostRollMs);
        var bytesToRemove = MillisecondsToBytes(extraSilenceMs);
        bytesToRemove -= bytesToRemove % BytesPerSample;

        if (bytesToRemove <= 0 || bytesToRemove >= _speechBuffer.Count)
        {
            return;
        }

        _speechBuffer.RemoveRange(_speechBuffer.Count - bytesToRemove, bytesToRemove);
    }
}

public sealed record VoiceActivityResult(
    double Rms,
    bool SpeechStarted,
    bool SpeechEnded,
    byte[]? UtteranceAudio)
{
    public static VoiceActivityResult None(double rms) => new(rms, false, false, null);
}
