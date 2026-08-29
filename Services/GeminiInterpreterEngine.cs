using System.Diagnostics;
using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class GeminiInterpreterEngine : IInterpreterEngine, IEngineInitializer
{
    private readonly GeminiApiClient _client;

    public GeminiInterpreterEngine(GeminiApiClient client)
    {
        _client = client;
    }

    public InterpreterEngineType EngineType => InterpreterEngineType.Gemini25Pro;

    public bool IsInitialized => _client.HasApiKey;

    public async Task<InterpreterResult?> ProcessAudioAsync(
        byte[] audioData,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var ai = Stopwatch.StartNew();
        var response = await _client.ProcessAudioAsync(audioData, cancellationToken).ConfigureAwait(false);
        ai.Stop();
        total.Stop();

        if (!response.Success)
        {
            return new InterpreterResult
            {
                Engine = EngineType,
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(response.Reason) ? "Đã bỏ qua" : response.Reason,
                AiProcessingMilliseconds = ai.Elapsed.TotalMilliseconds,
                TotalMilliseconds = total.Elapsed.TotalMilliseconds
            };
        }

        var sourceLanguage = LanguageHelper.ParseLanguage(response.DetectedLanguage);
        var targetLanguage = LanguageHelper.ParseLanguage(response.TargetLanguage);

        return new InterpreterResult
        {
            Engine = EngineType,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            OriginalText = response.OriginalText,
            TranslatedText = response.TranslatedText,
            AiProcessingMilliseconds = ai.Elapsed.TotalMilliseconds,
            TotalMilliseconds = total.Elapsed.TotalMilliseconds,
            Success = true
        };
    }
}
