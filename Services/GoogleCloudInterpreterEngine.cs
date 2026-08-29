using System.Diagnostics;
using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class GoogleCloudInterpreterEngine : IInterpreterEngine, IEngineInitializer
{
    private readonly GoogleTranslatePipeline _pipeline;
    private readonly InterpreterSettings _settings;
    private readonly AppLogger _logger;

    public GoogleCloudInterpreterEngine(
        GoogleTranslatePipeline pipeline,
        InterpreterSettings settings,
        AppLogger logger)
    {
        _pipeline = pipeline;
        _settings = settings;
        _logger = logger;
    }

    public InterpreterEngineType EngineType => InterpreterEngineType.GoogleCloudPipeline;

    public bool IsInitialized => _pipeline.IsInitialized;

    public async Task<InterpreterResult?> ProcessAudioAsync(
        byte[] audioData,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var recognitionWatch = Stopwatch.StartNew();
        var recognition = await _pipeline.RecognizeSpeechAsync(audioData, cancellationToken).ConfigureAwait(false);
        recognitionWatch.Stop();

        if (recognition is null || string.IsNullOrWhiteSpace(recognition.Text))
        {
            return new InterpreterResult
            {
                Engine = EngineType,
                Success = false,
                ErrorMessage = "Đã bỏ qua",
                RecognitionMilliseconds = recognitionWatch.Elapsed.TotalMilliseconds,
                TotalMilliseconds = total.Elapsed.TotalMilliseconds
            };
        }

        var sourceLanguage = recognition.Language == SupportedLanguage.Unknown
            ? InferLanguageFromText(recognition.Text)
            : recognition.Language;
        if (sourceLanguage == SupportedLanguage.Unknown)
        {
            return new InterpreterResult
            {
                Engine = EngineType,
                SourceLanguage = sourceLanguage,
                OriginalText = recognition.Text,
                Success = false,
                ErrorMessage = "Đã bỏ qua",
                RecognitionMilliseconds = recognitionWatch.Elapsed.TotalMilliseconds,
                TotalMilliseconds = total.Elapsed.TotalMilliseconds
            };
        }

        if (recognition.Confidence.HasValue && recognition.Confidence.Value < _settings.MinimumRecognitionConfidence)
        {
            _logger.Info($"Google Cloud nhận dạng độ tin cậy thấp nhưng vẫn xử lý: {recognition.Confidence.Value:0.000}");
        }

        var targetLanguage = LanguageHelper.GetTargetLanguage(sourceLanguage);
        var translationWatch = Stopwatch.StartNew();
        var translatedText = await _pipeline.TranslateTextAsync(
            recognition.Text,
            sourceLanguage,
            targetLanguage,
            cancellationToken).ConfigureAwait(false);
        translationWatch.Stop();
        total.Stop();

        return new InterpreterResult
        {
            Engine = EngineType,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            OriginalText = recognition.Text,
            TranslatedText = translatedText,
            Confidence = recognition.Confidence,
            RecognitionMilliseconds = recognitionWatch.Elapsed.TotalMilliseconds,
            TranslationMilliseconds = translationWatch.Elapsed.TotalMilliseconds,
            AiProcessingMilliseconds = recognitionWatch.Elapsed.TotalMilliseconds + translationWatch.Elapsed.TotalMilliseconds,
            TotalMilliseconds = total.Elapsed.TotalMilliseconds,
            Success = true
        };
    }

    private static SupportedLanguage InferLanguageFromText(string text)
    {
        foreach (var character in text)
        {
            if (character >= 0xAC00 && character <= 0xD7AF)
            {
                return SupportedLanguage.Korean;
            }
        }

        return SupportedLanguage.Vietnamese;
    }
}
