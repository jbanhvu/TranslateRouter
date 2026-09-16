using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class AdvancedVoiceActivityDetector
{
    private const int SampleRate = 16000;
    private const int BytesPerSample = 2;
    private const int StartConfirmationMs = 100;

    private readonly Queue<byte[]> _preRollBuffers = new();
    private readonly List<byte> _speechBuffer = new();
    private readonly object _syncRoot = new();
    private bool _isSpeechActive;
    private int _speechDurationMs;
    private int _voicedDurationMs;
    private int _silenceDurationMs;
    private int _speechCandidateDurationMs;
    private double _noiseFloor = 0.003;

    public AdvancedVoiceActivityDetector(InterpreterSettings settings)
    {
        Settings = settings;
    }

    public InterpreterSettings Settings { get; }

    public AdvancedVoiceActivityResult ProcessBuffer(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded <= 0)
        {
            return AdvancedVoiceActivityResult.None(0);
        }

        var rms = VoiceActivityDetector.CalculateRms(buffer, bytesRecorded);
        var bufferDurationMs = BytesToMilliseconds(bytesRecorded);
        byte[]? utterance = null;
        var started = false;
        var ended = false;

        lock (_syncRoot)
        {
            if (!_isSpeechActive)
            {
                var startThreshold = Math.Max(Settings.VadThreshold, _noiseFloor * 2.5);
                if (rms > startThreshold)
                {
                    _speechCandidateDurationMs += bufferDurationMs;
                }
                else
                {
                    _speechCandidateDurationMs = 0;
                    _noiseFloor = (_noiseFloor * 0.95) + (rms * 0.05);
                }

                StorePreRoll(buffer, bytesRecorded);
                if (_speechCandidateDurationMs >= StartConfirmationMs)
                {
                    _isSpeechActive = true;
                    _speechDurationMs = 0;
                    _voicedDurationMs = _speechCandidateDurationMs;
                    _silenceDurationMs = 0;
                    _speechBuffer.Clear();

                    foreach (var preRollBuffer in _preRollBuffers)
                    {
                        _speechBuffer.AddRange(preRollBuffer);
                    }

                    _preRollBuffers.Clear();
                    _speechCandidateDurationMs = 0;
                    started = true;
                }
            }
            else
            {
                _speechBuffer.AddRange(buffer.Take(bytesRecorded));
            }

            if (_isSpeechActive)
            {
                _speechDurationMs += bufferDurationMs;
                var endThreshold = Math.Max(Settings.VadThreshold * 0.65, _noiseFloor * 1.5);
                if (rms <= endThreshold)
                {
                    _silenceDurationMs += bufferDurationMs;
                }
                else
                {
                    _silenceDurationMs = 0;
                    if (!started)
                    {
                        _voicedDurationMs += bufferDurationMs;
                    }
                }

                var reachedSilenceEnd = _silenceDurationMs >= Settings.SilenceDurationMs;
                var reachedMaximum = _speechDurationMs >= Settings.MaximumSpeechDurationMs;
                if (reachedSilenceEnd || reachedMaximum)
                {
                    ended = true;
                    if (_voicedDurationMs >= Settings.MinimumSpeechDurationMs)
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

        return new AdvancedVoiceActivityResult(rms, started, ended, utterance);
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _preRollBuffers.Clear();
            _noiseFloor = 0.003;
            ResetSpeechOnly();
        }
    }

    private void StorePreRoll(byte[] buffer, int bytesRecorded)
    {
        var copy = new byte[bytesRecorded];
        Buffer.BlockCopy(buffer, 0, copy, 0, bytesRecorded);
        _preRollBuffers.Enqueue(copy);

        var maxBytes = MillisecondsToBytes(Settings.SpeechRecognition.PreRollMs);
        var totalBytes = _preRollBuffers.Sum(item => item.Length);
        while (totalBytes > maxBytes && _preRollBuffers.Count > 0)
        {
            totalBytes -= _preRollBuffers.Dequeue().Length;
        }
    }

    private void ResetSpeechOnly()
    {
        _isSpeechActive = false;
        _speechDurationMs = 0;
        _voicedDurationMs = 0;
        _silenceDurationMs = 0;
        _speechCandidateDurationMs = 0;
        _speechBuffer.Clear();
    }

    private void TrimTrailingSilenceToPostRoll()
    {
        var extraSilenceMs = Math.Max(0, Settings.SilenceDurationMs - Settings.SpeechRecognition.PostRollMs);
        var bytesToRemove = MillisecondsToBytes(extraSilenceMs);
        bytesToRemove -= bytesToRemove % BytesPerSample;
        if (bytesToRemove > 0 && bytesToRemove < _speechBuffer.Count)
        {
            _speechBuffer.RemoveRange(_speechBuffer.Count - bytesToRemove, bytesToRemove);
        }
    }

    private static int BytesToMilliseconds(int bytes)
        => (int)Math.Round(bytes / (double)(SampleRate * BytesPerSample) * 1000);

    private static int MillisecondsToBytes(int milliseconds)
        => SampleRate * BytesPerSample * milliseconds / 1000;
}

public sealed record AdvancedVoiceActivityResult(
    double Rms,
    bool SpeechStarted,
    bool SpeechEnded,
    byte[]? UtteranceAudio)
{
    public static AdvancedVoiceActivityResult None(double rms) => new(rms, false, false, null);
}
