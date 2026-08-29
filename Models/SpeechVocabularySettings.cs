using System.Text.Json;
using MeetingInterpreter.Services;

namespace MeetingInterpreter.Models;

public sealed class SpeechVocabularySettings
{
    public List<string> Phrases { get; set; } = new();

    public float Boost { get; set; } = 8.0f;

    public static SpeechVocabularySettings Load(string path, AppLogger? logger = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new SpeechVocabularySettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SpeechVocabularySettings>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new SpeechVocabularySettings();
        }
        catch (Exception ex)
        {
            logger?.Error("Khong the doc speech-vocabulary.json.", ex);
            return new SpeechVocabularySettings();
        }
    }
}
