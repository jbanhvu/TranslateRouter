using MeetingInterpreter.Models;
using NAudio.Wave;

namespace MeetingInterpreter.Services;

public static class AudioAnalysisHelper
{
    private const int BytesPerSample = 2;

    public static AudioAnalysisResult AnalyzeLinear16Mono(
        byte[] audioData,
        int sampleRate = 16000,
        int channels = 1,
        int bitsPerSample = 16)
    {
        if (audioData.Length < BytesPerSample)
        {
            return new AudioAnalysisResult
            {
                SampleRate = sampleRate,
                Channels = channels,
                BitsPerSample = bitsPerSample,
                Bytes = audioData.Length
            };
        }

        double sumSquares = 0;
        double peak = 0;
        var samples = audioData.Length / BytesPerSample;

        for (var i = 0; i < samples * BytesPerSample; i += BytesPerSample)
        {
            var normalized = Math.Abs(BitConverter.ToInt16(audioData, i) / 32768.0);
            sumSquares += normalized * normalized;
            peak = Math.Max(peak, normalized);
        }

        return new AudioAnalysisResult
        {
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            Bytes = audioData.Length,
            DurationMs = samples / (double)(sampleRate * channels) * 1000,
            Rms = Math.Sqrt(sumSquares / samples),
            Peak = peak
        };
    }

    public static string GetLevelStatus(AudioAnalysisResult analysis)
    {
        if (analysis.Rms < 0.012 || analysis.Peak < 0.08)
        {
            return "Am luong qua nho";
        }

        if (analysis.Peak > 0.95)
        {
            return "Am luong qua lon";
        }

        return "Am luong tot";
    }

    public static void SaveDebugWave(byte[] audioData, string directoryPath, string fileName)
    {
        Directory.CreateDirectory(directoryPath);
        var path = Path.Combine(directoryPath, fileName);
        using var writer = new WaveFileWriter(path, new WaveFormat(rate: 16000, bits: 16, channels: 1));
        writer.Write(audioData, 0, audioData.Length);
    }

    public static byte[] WrapPcm16MonoAsWave(byte[] pcmData, int sampleRate)
    {
        using var memoryStream = new MemoryStream();
        using (var writer = new WaveFileWriter(memoryStream, new WaveFormat(rate: sampleRate, bits: 16, channels: 1)))
        {
            writer.Write(pcmData, 0, pcmData.Length);
        }

        return memoryStream.ToArray();
    }
}
