using Google.Api.Gax.Grpc;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Speech.V1;
using Google.Cloud.TextToSpeech.V1;
using Google.Cloud.Translation.V2;
using Google.Protobuf;
using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class GoogleTranslatePipeline : ISpeechRecognitionService
{
    private readonly AppLogger? _logger;
    private readonly SpeechRecognitionSettings _speechSettings;
    private readonly SpeechVocabularySettings _vocabularySettings;
    private SpeechClient? _speechClient;
    private TranslationClient? _translationClient;
    private TextToSpeechClient? _textToSpeechClient;
    private string? _credentialPath;

    public GoogleTranslatePipeline(
        AppLogger? logger = null,
        SpeechRecognitionSettings? speechSettings = null,
        SpeechVocabularySettings? vocabularySettings = null)
    {
        _logger = logger;
        _speechSettings = speechSettings ?? new SpeechRecognitionSettings();
        _vocabularySettings = vocabularySettings ?? new SpeechVocabularySettings();
    }

    public bool IsInitialized => _speechClient is not null
        && _translationClient is not null
        && _textToSpeechClient is not null;

    public SpeechRecognitionSettings SpeechSettings => _speechSettings;

    public async Task InitializeAsync(string credentialPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(credentialPath))
        {
            throw new FileNotFoundException("Không tìm thấy tệp xác thực Google Cloud.", credentialPath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        _speechClient = await new SpeechClientBuilder
        {
            CredentialsPath = credentialPath
        }.BuildAsync(cancellationToken).ConfigureAwait(false);

        _textToSpeechClient = await new TextToSpeechClientBuilder
        {
            CredentialsPath = credentialPath
        }.BuildAsync(cancellationToken).ConfigureAwait(false);

        var credential = await CredentialFactory
            .FromFileAsync<ServiceAccountCredential>(credentialPath, cancellationToken)
            .ConfigureAwait(false);
        _translationClient = await TranslationClient.CreateAsync(
            credential.ToGoogleCredential()).ConfigureAwait(false);

        _credentialPath = credentialPath;
    }

    public Task ReinitializeIfNeededAsync(string credentialPath, CancellationToken cancellationToken)
    {
        if (IsInitialized && string.Equals(_credentialPath, credentialPath, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        return InitializeAsync(credentialPath, cancellationToken);
    }

    public Task<SpeechRecognitionResultModel?> RecognizeSpeechAsync(
        byte[] audioData,
        CancellationToken cancellationToken)
        => RecognizeSpeechAsync(
            audioData,
            SupportedLanguage.Vietnamese,
            includeAlternativeLanguage: true,
            cancellationToken: cancellationToken);

    public async Task<SpeechRecognitionResultModel?> RecognizeSpeechAsync(
        byte[] audioData,
        SupportedLanguage primaryLanguage,
        bool includeAlternativeLanguage,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var analysis = AudioAnalysisHelper.AnalyzeLinear16Mono(audioData, _speechSettings.SampleRate);
        _logger?.Info(
            "STT audio: " +
            $"bytes={analysis.Bytes}, durationMs={analysis.DurationMs:0}, " +
            $"sampleRate={analysis.SampleRate}, channels={analysis.Channels}, bits={analysis.BitsPerSample}, " +
            $"rms={analysis.Rms:0.0000}, peak={analysis.Peak:0.0000}, level={AudioAnalysisHelper.GetLevelStatus(analysis)}");

        if (_speechSettings.SaveRecognitionAudioForDebug)
        {
            var debugDirectory = Path.Combine(AppContext.BaseDirectory, "DebugAudio");
            AudioAnalysisHelper.SaveDebugWave(
                audioData,
                debugDirectory,
                $"utterance-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
        }

        var config = CreateRecognitionConfig(
            primaryLanguage: primaryLanguage,
            includeAlternativeLanguage: includeAlternativeLanguage);

        var audio = new RecognitionAudio
        {
            Content = ByteString.CopyFrom(audioData)
        };

        var response = await _speechClient!.RecognizeAsync(
            config,
            audio,
            CallSettings.FromCancellationToken(cancellationToken)).ConfigureAwait(false);

        var recognizedSegments = response.Results
            .Select(result => new
            {
                Result = result,
                Alternatives = result.Alternatives
                    .Take(Math.Max(1, _speechSettings.MaxAlternatives))
                    .Select(alternative => new SpeechAlternativeModel
                    {
                        Text = NormalizeTranscript(alternative.Transcript),
                        Confidence = alternative.Confidence > 0 ? alternative.Confidence : null
                    })
                    .Where(alternative => !string.IsNullOrWhiteSpace(alternative.Text))
                    .ToList()
            })
            .Where(segment => segment.Alternatives.Count > 0)
            .ToList();

        if (recognizedSegments.Count == 0)
        {
            return null;
        }

        var primaryTexts = recognizedSegments
            .Select(segment => segment.Alternatives
                .OrderByDescending(alternative => alternative.Confidence ?? -1)
                .First())
            .ToList();
        var text = NormalizeTranscript(string.Join(" ", primaryTexts.Select(alternative => alternative.Text)));
        var confidence = primaryTexts
            .Where(alternative => alternative.Confidence.HasValue)
            .Select(alternative => alternative.Confidence!.Value)
            .DefaultIfEmpty()
            .Average();
        float? nullableConfidence = confidence > 0 ? confidence : null;
        var rawLanguageCode = recognizedSegments
            .Select(segment => segment.Result.LanguageCode)
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code)) ?? string.Empty;
        var language = LanguageHelper.ParseLanguage(rawLanguageCode);

        _logger?.Info(
            "STT result: " +
            $"model={_speechSettings.Model}, language={language}, rawLanguage={rawLanguageCode}, " +
            $"confidence={(nullableConfidence.HasValue ? nullableConfidence.Value.ToString("0.000") : "null")}, " +
            $"text={text}, alternatives={string.Join(" | ", primaryTexts.Select(item => $"{item.Text} ({item.Confidence?.ToString("0.000") ?? "null"})"))}");

        return new SpeechRecognitionResultModel
        {
            Text = text,
            Confidence = nullableConfidence,
            Language = language,
            RawLanguageCode = rawLanguageCode,
            Alternatives = recognizedSegments.SelectMany(segment => segment.Alternatives).Take(3).ToList()
        };
    }

    public RecognitionConfig CreateRecognitionConfig(
        string? modelOverride = null,
        SupportedLanguage primaryLanguage = SupportedLanguage.Vietnamese,
        bool includeAlternativeLanguage = true)
    {
        var primaryLanguageCode = LanguageHelper.ToGoogleSpeechCode(primaryLanguage);
        if (string.IsNullOrWhiteSpace(primaryLanguageCode))
        {
            primaryLanguageCode = "vi-VN";
            primaryLanguage = SupportedLanguage.Vietnamese;
        }

        var alternativeLanguage = primaryLanguage == SupportedLanguage.Korean
            ? SupportedLanguage.Vietnamese
            : SupportedLanguage.Korean;

        var config = new RecognitionConfig
        {
            Encoding = RecognitionConfig.Types.AudioEncoding.Linear16,
            SampleRateHertz = _speechSettings.SampleRate,
            AudioChannelCount = 1,
            LanguageCode = primaryLanguageCode,
            Model = string.IsNullOrWhiteSpace(modelOverride) ? _speechSettings.Model : modelOverride,
            EnableAutomaticPunctuation = _speechSettings.EnableAutomaticPunctuation,
            MaxAlternatives = Math.Clamp(_speechSettings.MaxAlternatives, 1, 3)
        };
        if (includeAlternativeLanguage)
        {
            config.AlternativeLanguageCodes.Add(LanguageHelper.ToGoogleSpeechCode(alternativeLanguage));
        }
        AddVocabularyAdaptation(config);
        return config;
    }

    public StreamingRecognitionConfig CreateStreamingRecognitionConfig(
        SupportedLanguage primaryLanguage = SupportedLanguage.Vietnamese)
        => new()
        {
            Config = CreateRecognitionConfig(primaryLanguage: primaryLanguage),
            InterimResults = true,
            SingleUtterance = false
        };

    public SpeechClient.StreamingRecognizeStream CreateStreamingRecognizeStream(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return _speechClient!.StreamingRecognize(CallSettings.FromCancellationToken(cancellationToken));
    }

    public async Task<IReadOnlyList<SpeechRecognitionResultModel?>> BenchmarkModelsAsync(
        byte[] audioData,
        IEnumerable<string> models,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var results = new List<SpeechRecognitionResultModel?>();
        foreach (var model in models.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var original = _speechSettings.Model;
            try
            {
                _speechSettings.Model = model;
                results.Add(await RecognizeSpeechAsync(audioData, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                _speechSettings.Model = original;
            }
        }

        return results;
    }

    public async Task<string> TranslateTextAsync(
        string text,
        SupportedLanguage sourceLanguage,
        SupportedLanguage targetLanguage,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var sourceCode = LanguageHelper.ToGoogleTranslateCode(sourceLanguage);
        var targetCode = LanguageHelper.ToGoogleTranslateCode(targetLanguage);

        if (string.IsNullOrWhiteSpace(sourceCode) || string.IsNullOrWhiteSpace(targetCode) || sourceCode == targetCode)
        {
            throw new InvalidOperationException("Hướng dịch không hợp lệ.");
        }

        var response = await _translationClient!.TranslateTextAsync(
            text,
            targetCode,
            sourceCode,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.TranslatedText;
    }

    public async Task<byte[]> SynthesizeSpeechAsync(
        string text,
        SupportedLanguage targetLanguage,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var languageCode = LanguageHelper.ToGoogleSpeechCode(targetLanguage);
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            throw new InvalidOperationException("Ngôn ngữ tạo giọng nói không được hỗ trợ.");
        }

        var response = await _textToSpeechClient!.SynthesizeSpeechAsync(
            new SynthesisInput { Text = text },
            new VoiceSelectionParams
            {
                LanguageCode = languageCode,
                SsmlGender = SsmlVoiceGender.Neutral
            },
            new AudioConfig
            {
                AudioEncoding = AudioEncoding.Linear16
            },
            CallSettings.FromCancellationToken(cancellationToken)).ConfigureAwait(false);

        return response.AudioContent.ToByteArray();
    }

    private void EnsureInitialized()
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException("Google Cloud chưa được cấu hình.");
        }
    }

    private void AddVocabularyAdaptation(RecognitionConfig config)
    {
        var phrases = _vocabularySettings.Phrases
            .Select(phrase => phrase.Trim())
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();

        if (phrases.Count == 0)
        {
            return;
        }

        var boost = Math.Clamp(_vocabularySettings.Boost, 5.0f, 10.0f);
        var phraseSet = new PhraseSet { Boost = boost };
        phraseSet.Phrases.AddRange(phrases.Select(phrase => new PhraseSet.Types.Phrase
        {
            Value = phrase,
            Boost = boost
        }));

        config.Adaptation = new SpeechAdaptation();
        config.Adaptation.PhraseSets.Add(phraseSet);
    }

    private static string NormalizeTranscript(string text)
        => string.Join(" ", text.Trim().Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
}
