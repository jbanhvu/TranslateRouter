using System.Diagnostics;
using System.Text;
using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class AdaptiveHybridCoordinator
{
    private static readonly TimeSpan LanguageDecisionDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan MaximumSentenceDuration = TimeSpan.FromSeconds(25);
    private const int MaximumSentenceCharacters = 1200;
    private const float VerificationConfidenceThreshold = 0.78f;

    private readonly GoogleTranslatePipeline _pipeline;
    private readonly AppLogger _logger;
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _finalizeCts;
    private DateTime _speechStartedAtUtc;
    private DateTime _aggregateStartedAtUtc;
    private DateTime _aggregateCreatedAt;
    private SupportedLanguage _lockedLanguage;
    private SupportedLanguage _languageCandidate;
    private int _languageCandidateStreak;
    private string _latestInterimText = string.Empty;
    private SupportedLanguage _latestInterimLanguage;
    private string _aggregateText = string.Empty;
    private string _lastStreamingSegment = string.Empty;
    private SupportedLanguage _aggregateLanguage;
    private double _confidenceTotal;
    private int _confidenceCount;
    private double _recognitionMilliseconds;
    private int _fragmentCount;
    private bool _speechActive;
    private bool _awaitingFinalAfterSpeech;
    private bool _verificationRequested;
    private bool _verificationInFlight;
    private byte[]? _lastUtteranceAudio;
    private readonly List<byte> _aggregateAudio = new();
    private long _streamFinalVersion;
    private long _streamFinalVersionAtSpeechStart;
    private long _verificationGeneration;
    private long _sequence;
    private AdaptiveHybridFinalizeMode _finalizeMode;
    private bool _physicalMuteCommitPending;

    public AdaptiveHybridCoordinator(GoogleTranslatePipeline pipeline, AppLogger logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    public event EventHandler<InterpreterContentPreviewEventArgs>? PreviewChanged;

    public event Action<TranscriptUtterance>? TranscriptReady;

    public event EventHandler<string>? StatusChanged;

    public void Start(
        CancellationToken cancellationToken,
        AdaptiveHybridFinalizeMode finalizeMode = AdaptiveHybridFinalizeMode.AutomaticAfterSpeech)
    {
        lock (_syncRoot)
        {
            StopLocked();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _finalizeMode = finalizeMode;
            ResetAllLocked();
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            StopLocked();
            ResetAllLocked();
        }
    }

    public void OnSpeechStarted()
    {
        lock (_syncRoot)
        {
            if (_sessionCts is null || _sessionCts.IsCancellationRequested)
            {
                return;
            }

            _speechActive = true;
            _awaitingFinalAfterSpeech = false;
            _speechStartedAtUtc = DateTime.UtcNow;
            _streamFinalVersionAtSpeechStart = _streamFinalVersion;
            _lastUtteranceAudio = null;
            if (!_physicalMuteCommitPending)
            {
                CancelFinalizeLocked();
            }
            if (string.IsNullOrWhiteSpace(_aggregateText))
            {
                ResetLanguageGateLocked();
            }
        }

        StatusChanged?.Invoke(this, "Đang xác định ngôn ngữ và nhận dạng trực tiếp...");
    }

    public void OnSpeechEnded(byte[]? utteranceAudio)
    {
        byte[]? audioToVerify = null;
        long finalVersionAtEnd = 0;
        var fallbackIfNoFinal = false;
        lock (_syncRoot)
        {
            if (_sessionCts is null || _sessionCts.IsCancellationRequested)
            {
                return;
            }

            _speechActive = false;
            _awaitingFinalAfterSpeech = true;
            if (utteranceAudio is not null)
            {
                _aggregateAudio.AddRange(utteranceAudio);
                _lastUtteranceAudio = _aggregateAudio.ToArray();
            }
            finalVersionAtEnd = _streamFinalVersion;
            fallbackIfNoFinal = finalVersionAtEnd == _streamFinalVersionAtSpeechStart;
            var averageConfidence = _confidenceCount == 0
                ? (float?)null
                : (float)(_confidenceTotal / _confidenceCount);
            var longSentenceNeedsVerification = CountWords(_aggregateText) >= 18
                && (!averageConfidence.HasValue || averageConfidence.Value < 0.82f);
            _verificationRequested |= longSentenceNeedsVerification;
            if (_lastUtteranceAudio is not null && (_verificationRequested || fallbackIfNoFinal))
            {
                audioToVerify = _lastUtteranceAudio;
            }

            if (_finalizeMode == AdaptiveHybridFinalizeMode.AutomaticAfterSpeech
                && !string.IsNullOrWhiteSpace(_aggregateText))
            {
                ScheduleFinalizeLocked(GetContinuationWindow(_aggregateText));
            }
        }

        if (audioToVerify is not null)
        {
            StartSelectiveVerification(audioToVerify, finalVersionAtEnd, fallbackIfNoFinal);
        }
    }

    public void OnInterimTranscript(StreamingTranscriptEventArgs args)
    {
        InterpreterContentPreviewEventArgs? preview = null;
        lock (_syncRoot)
        {
            if (!CanAcceptStreamingResultLocked(args.Text))
            {
                return;
            }

            var language = ResolveLanguage(args);
            UpdateLanguageGateLocked(language, args.Text, force: false);
            _latestInterimText = PreferMoreCompleteText(_latestInterimText, args.Text);
            _latestInterimLanguage = language;
            if (_lockedLanguage == SupportedLanguage.Unknown || language != _lockedLanguage)
            {
                return;
            }

            var content = _aggregateLanguage == language
                ? JoinFragments(_aggregateText, _latestInterimText)
                : _latestInterimText.Trim();
            preview = new InterpreterContentPreviewEventArgs(
                "Đang nghe trực tiếp",
                content,
                isTranslation: false,
                isInterim: true,
                language: language);
        }

        PreviewChanged?.Invoke(this, preview);
    }

    public void OnFinalTranscript(StreamingTranscriptEventArgs args)
    {
        TranscriptUtterance? completedBeforeNewLanguage = null;
        InterpreterContentPreviewEventArgs? preview = null;
        byte[]? audioToVerify = null;
        long finalVersion = 0;
        lock (_syncRoot)
        {
            if (!CanAcceptStreamingResultLocked(args.Text))
            {
                return;
            }

            var language = ResolveLanguage(args);
            if (language == SupportedLanguage.Unknown)
            {
                _verificationRequested = true;
                return;
            }

            UpdateLanguageGateLocked(language, args.Text, force: true);
            var shouldStartNewSentence = !string.IsNullOrWhiteSpace(_aggregateText)
                && (_aggregateLanguage != language
                    || DateTime.UtcNow - _aggregateStartedAtUtc >= MaximumSentenceDuration
                    || _aggregateText.Length + args.Text.Length > MaximumSentenceCharacters);
            if (shouldStartNewSentence)
            {
                completedBeforeNewLanguage = CompleteSentenceLocked();
            }

            AddStreamingFinalLocked(args.Text.Trim(), language, args.Confidence);
            _streamFinalVersion++;
            finalVersion = _streamFinalVersion;

            var interimWasLonger = _latestInterimLanguage == language
                && CountWords(_latestInterimText) >= CountWords(args.Text) + 3
                && AreRelated(_latestInterimText, args.Text);
            var lowConfidence = args.Confidence.HasValue
                && args.Confidence.Value < VerificationConfidenceThreshold;
            _verificationRequested |= interimWasLonger || lowConfidence;
            _latestInterimText = string.Empty;
            _latestInterimLanguage = SupportedLanguage.Unknown;
            if (_verificationRequested && _lastUtteranceAudio is not null)
            {
                audioToVerify = _lastUtteranceAudio;
            }

            if (_finalizeMode == AdaptiveHybridFinalizeMode.AutomaticAfterSpeech && !_speechActive)
            {
                ScheduleFinalizeLocked(GetContinuationWindow(_aggregateText));
            }

            preview = new InterpreterContentPreviewEventArgs(
                "Nội dung đang gom",
                _aggregateText,
                isTranslation: false,
                isInterim: true,
                language: _aggregateLanguage);
        }

        if (completedBeforeNewLanguage is not null)
        {
            TranscriptReady?.Invoke(completedBeforeNewLanguage);
        }

        PreviewChanged?.Invoke(this, preview);
        if (audioToVerify is not null)
        {
            StartSelectiveVerification(audioToVerify, finalVersion, fallbackIfNoFinal: false);
        }
    }

    public void OnPhysicalMuteDetected()
    {
        lock (_syncRoot)
        {
            if (_finalizeMode != AdaptiveHybridFinalizeMode.PhysicalMuteOnly
                || _sessionCts is null
                || _sessionCts.IsCancellationRequested)
            {
                return;
            }

            _speechActive = false;
            _awaitingFinalAfterSpeech = true;
            _physicalMuteCommitPending = true;
            ScheduleFinalizeLocked(TimeSpan.FromMilliseconds(850), force: true);
        }

        StatusChanged?.Invoke(this, "Đã phát hiện micro vật lý tắt. Đang chốt nội dung để dịch...");
    }

    public void OnPhysicalInputResumed()
    {
        if (_finalizeMode == AdaptiveHybridFinalizeMode.PhysicalMuteOnly)
        {
            StatusChanged?.Invoke(this, "Micro đang hoạt động. Hệ thống chỉ hiển thị transcript và chờ tắt micro để dịch.");
        }
    }

    private void StartSelectiveVerification(byte[] audioData, long streamVersion, bool fallbackIfNoFinal)
    {
        long generation;
        CancellationToken cancellationToken;
        SupportedLanguage verificationLanguage;
        lock (_syncRoot)
        {
            if (_sessionCts is null || _sessionCts.IsCancellationRequested || _verificationInFlight)
            {
                return;
            }

            _verificationInFlight = true;
            generation = ++_verificationGeneration;
            cancellationToken = _sessionCts.Token;
            verificationLanguage = _lockedLanguage != SupportedLanguage.Unknown
                ? _lockedLanguage
                : _aggregateLanguage;
        }

        _ = Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (fallbackIfNoFinal)
                {
                    await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                    lock (_syncRoot)
                    {
                        if (_streamFinalVersion > streamVersion && !_verificationRequested)
                        {
                            _verificationInFlight = false;
                            return;
                        }
                    }
                }

                StatusChanged?.Invoke(this, "Đang kiểm tra lại câu có độ tin cậy thấp...");
                var recognition = verificationLanguage == SupportedLanguage.Unknown
                    ? await _pipeline
                        .RecognizeSpeechAsync(audioData, cancellationToken)
                        .ConfigureAwait(false)
                    : await _pipeline
                        .RecognizeSpeechAsync(
                            audioData,
                            verificationLanguage,
                            includeAlternativeLanguage: false,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                stopwatch.Stop();
                if (recognition is null || string.IsNullOrWhiteSpace(recognition.Text))
                {
                    FinishVerificationWithoutResult(generation);
                    return;
                }

                ApplyVerificationResult(generation, recognition, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.Error("[Adaptive Hybrid] Selective STT verification failed; keeping streaming transcript.", ex);
                FinishVerificationWithoutResult(generation);
            }
        }, CancellationToken.None);
    }

    private void ApplyVerificationResult(
        long generation,
        SpeechRecognitionResultModel recognition,
        double recognitionMilliseconds)
    {
        InterpreterContentPreviewEventArgs? preview = null;
        lock (_syncRoot)
        {
            if (generation != _verificationGeneration || _sessionCts is null || _sessionCts.IsCancellationRequested)
            {
                return;
            }

            var language = recognition.Language == SupportedLanguage.Unknown
                ? InferLanguage(recognition.Text)
                : recognition.Language;
            if (language == SupportedLanguage.Unknown)
            {
                _verificationInFlight = false;
                _verificationRequested = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(_aggregateText))
            {
                StartAggregateLocked(recognition.Text.Trim(), language, recognition.Confidence);
            }
            else if (_aggregateLanguage == language)
            {
                var verifiedText = recognition.Text.Trim();
                if (AreRelated(_aggregateText, verifiedText)
                    && CountWords(verifiedText) >= CountWords(_aggregateText))
                {
                    _aggregateText = verifiedText;
                    _lastStreamingSegment = verifiedText;
                    _fragmentCount = 1;
                }
                else
                {
                    ApplyVerifiedSegmentLocked(verifiedText);
                }
                AddConfidenceLocked(recognition.Confidence);
            }
            else if (_fragmentCount <= 1)
            {
                _aggregateText = recognition.Text.Trim();
                _aggregateLanguage = language;
                _lockedLanguage = language;
                _lastStreamingSegment = recognition.Text.Trim();
            }

            _recognitionMilliseconds += recognitionMilliseconds;
            _verificationInFlight = false;
            _verificationRequested = false;
            if (_finalizeMode == AdaptiveHybridFinalizeMode.AutomaticAfterSpeech)
            {
                ScheduleFinalizeLocked(TimeSpan.FromMilliseconds(300));
            }
            else if (_physicalMuteCommitPending)
            {
                ScheduleFinalizeLocked(TimeSpan.FromMilliseconds(150), force: true);
            }
            preview = new InterpreterContentPreviewEventArgs(
                "Nội dung đã kiểm tra",
                _aggregateText,
                isTranslation: false,
                isInterim: true,
                language: _aggregateLanguage);
            _logger.Info($"[Adaptive Hybrid] Selective verification accepted. Language={language}; Text={recognition.Text}");
        }

        PreviewChanged?.Invoke(this, preview);
    }

    private void FinishVerificationWithoutResult(long generation)
    {
        lock (_syncRoot)
        {
            if (generation != _verificationGeneration)
            {
                return;
            }

            _verificationInFlight = false;
            _verificationRequested = false;
            if (_finalizeMode == AdaptiveHybridFinalizeMode.AutomaticAfterSpeech
                && !string.IsNullOrWhiteSpace(_aggregateText))
            {
                ScheduleFinalizeLocked(TimeSpan.FromMilliseconds(300));
            }
            else if (_physicalMuteCommitPending)
            {
                ScheduleFinalizeLocked(TimeSpan.FromMilliseconds(150), force: true);
            }
        }
    }

    private void AddStreamingFinalLocked(string text, SupportedLanguage language, float? confidence)
    {
        if (string.IsNullOrWhiteSpace(_aggregateText))
        {
            StartAggregateLocked(text, language, confidence);
            return;
        }

        _aggregateText = JoinFragments(_aggregateText, text);
        _lastStreamingSegment = text;
        _fragmentCount++;
        AddConfidenceLocked(confidence);
    }

    private void StartAggregateLocked(string text, SupportedLanguage language, float? confidence)
    {
        _aggregateText = text.Trim();
        _lastStreamingSegment = text.Trim();
        _aggregateLanguage = language;
        _lockedLanguage = language;
        _aggregateCreatedAt = DateTime.Now;
        _aggregateStartedAtUtc = DateTime.UtcNow;
        _fragmentCount = 1;
        _confidenceTotal = 0;
        _confidenceCount = 0;
        AddConfidenceLocked(confidence);
    }

    private void ApplyVerifiedSegmentLocked(string verifiedText)
    {
        if (AreRelated(_lastStreamingSegment, verifiedText))
        {
            if (CountWords(verifiedText) >= CountWords(_lastStreamingSegment))
            {
                _aggregateText = ReplaceTrailingSegment(_aggregateText, _lastStreamingSegment, verifiedText);
                _lastStreamingSegment = verifiedText;
            }
            return;
        }

        if (_fragmentCount == 1 && CountWords(verifiedText) >= CountWords(_aggregateText))
        {
            _aggregateText = verifiedText;
            _lastStreamingSegment = verifiedText;
        }
    }

    private void ScheduleFinalizeLocked(TimeSpan delay, bool force = false)
    {
        if (_sessionCts is null
            || _sessionCts.IsCancellationRequested
            || (!force && string.IsNullOrWhiteSpace(_aggregateText)))
        {
            return;
        }

        CancelFinalizeLocked();
        _finalizeCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        var token = _finalizeCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                TranscriptUtterance? transcript;
                lock (_syncRoot)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_verificationInFlight)
                    {
                        ScheduleFinalizeLocked(TimeSpan.FromMilliseconds(250), force);
                        return;
                    }

                    if (!force && _speechActive)
                    {
                        ScheduleFinalizeLocked(TimeSpan.FromMilliseconds(250));
                        return;
                    }

                    if (force && string.IsNullOrWhiteSpace(_aggregateText))
                    {
                        PromoteLatestInterimLocked();
                    }

                    transcript = CompleteSentenceLocked();
                    if (force)
                    {
                        _physicalMuteCommitPending = false;
                    }
                }

                if (transcript is not null)
                {
                    TranscriptReady?.Invoke(transcript);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private void PromoteLatestInterimLocked()
    {
        if (string.IsNullOrWhiteSpace(_latestInterimText))
        {
            return;
        }

        var language = _latestInterimLanguage != SupportedLanguage.Unknown
            ? _latestInterimLanguage
            : InferLanguage(_latestInterimText);
        if (language != SupportedLanguage.Unknown)
        {
            StartAggregateLocked(_latestInterimText, language, confidence: null);
        }
    }

    private TranscriptUtterance? CompleteSentenceLocked()
    {
        if (string.IsNullOrWhiteSpace(_aggregateText) || _aggregateLanguage == SupportedLanguage.Unknown)
        {
            ResetAggregateLocked();
            return null;
        }

        var transcript = new TranscriptUtterance
        {
            SequenceNumber = ++_sequence,
            CreatedAt = _aggregateCreatedAt,
            Text = _aggregateText.Trim(),
            SourceLanguage = _aggregateLanguage,
            TargetLanguage = LanguageHelper.GetTargetLanguage(_aggregateLanguage),
            Confidence = _confidenceCount == 0 ? null : (float)(_confidenceTotal / _confidenceCount),
            RecognitionMilliseconds = _recognitionMilliseconds
        };
        _logger.Info(
            $"[Adaptive Hybrid #{transcript.SequenceNumber}] Canonical sentence ready. " +
            $"Fragments={_fragmentCount}; Language={transcript.SourceLanguage}; Text={transcript.Text}");
        ResetAggregateLocked();
        return transcript;
    }

    private void UpdateLanguageGateLocked(SupportedLanguage language, string text, bool force)
    {
        if (language == SupportedLanguage.Unknown)
        {
            return;
        }

        if (_lockedLanguage != SupportedLanguage.Unknown)
        {
            return;
        }

        if (_languageCandidate == language)
        {
            _languageCandidateStreak++;
        }
        else
        {
            _languageCandidate = language;
            _languageCandidateStreak = 1;
        }

        var elapsed = DateTime.UtcNow - _speechStartedAtUtc;
        if (force || (elapsed >= LanguageDecisionDelay && _languageCandidateStreak >= 2 && CountWords(text) >= 3))
        {
            _lockedLanguage = language;
            _logger.Info($"[Adaptive Hybrid] Language locked: {language}; streak={_languageCandidateStreak}; text={text}");
        }
    }

    private bool CanAcceptStreamingResultLocked(string text)
        => _sessionCts is not null
            && !_sessionCts.IsCancellationRequested
            && !string.IsNullOrWhiteSpace(text)
            && (_speechActive || _awaitingFinalAfterSpeech || !string.IsNullOrWhiteSpace(_aggregateText));

    private void AddConfidenceLocked(float? confidence)
    {
        if (!confidence.HasValue)
        {
            return;
        }

        _confidenceTotal += confidence.Value;
        _confidenceCount++;
    }

    private void ResetLanguageGateLocked()
    {
        _lockedLanguage = SupportedLanguage.Unknown;
        _languageCandidate = SupportedLanguage.Unknown;
        _languageCandidateStreak = 0;
        _latestInterimText = string.Empty;
        _latestInterimLanguage = SupportedLanguage.Unknown;
    }

    private void ResetAggregateLocked()
    {
        CancelFinalizeLocked();
        _aggregateText = string.Empty;
        _lastStreamingSegment = string.Empty;
        _aggregateLanguage = SupportedLanguage.Unknown;
        _aggregateStartedAtUtc = default;
        _aggregateCreatedAt = default;
        _confidenceTotal = 0;
        _confidenceCount = 0;
        _recognitionMilliseconds = 0;
        _fragmentCount = 0;
        _verificationRequested = false;
        _physicalMuteCommitPending = false;
        _lastUtteranceAudio = null;
        _aggregateAudio.Clear();
        _awaitingFinalAfterSpeech = false;
    }

    private void ResetAllLocked()
    {
        _sequence = 0;
        _streamFinalVersion = 0;
        _streamFinalVersionAtSpeechStart = 0;
        _verificationGeneration = 0;
        _verificationInFlight = false;
        _speechActive = false;
        _awaitingFinalAfterSpeech = false;
        ResetLanguageGateLocked();
        ResetAggregateLocked();
    }

    private void StopLocked()
    {
        CancelFinalizeLocked();
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
    }

    private void CancelFinalizeLocked()
    {
        _finalizeCts?.Cancel();
        _finalizeCts?.Dispose();
        _finalizeCts = null;
    }

    private static SupportedLanguage ResolveLanguage(StreamingTranscriptEventArgs args)
    {
        if (ContainsHangul(args.Text))
        {
            return SupportedLanguage.Korean;
        }

        if (args.Language is SupportedLanguage.Vietnamese or SupportedLanguage.Korean)
        {
            return args.Language;
        }

        return string.IsNullOrWhiteSpace(args.Text) ? SupportedLanguage.Unknown : SupportedLanguage.Vietnamese;
    }

    private static SupportedLanguage InferLanguage(string text)
        => ContainsHangul(text) ? SupportedLanguage.Korean : SupportedLanguage.Vietnamese;

    private static TimeSpan GetContinuationWindow(string text)
    {
        var normalized = text.Trim();
        if (normalized.EndsWith('?') || normalized.EndsWith('!'))
        {
            return TimeSpan.FromMilliseconds(650);
        }

        if (LooksIncomplete(normalized))
        {
            return TimeSpan.FromMilliseconds(1800);
        }

        return normalized.EndsWith('.')
            ? TimeSpan.FromMilliseconds(750)
            : TimeSpan.FromMilliseconds(1200);
    }

    private static bool LooksIncomplete(string text)
    {
        var normalized = text.Trim().TrimEnd('.', ',', ';', ':', '…').Trim().ToLowerInvariant();
        string[] endings =
        [
            "và", "nhưng", "vì", "nếu", "thì", "để", "khi", "mà", "là", "của", "với", "cho",
            "từ", "đến", "về", "trong", "theo", "hoặc", "do", "bởi vì", "tuy nhiên",
            "그리고", "하지만", "때문에", "만약", "그러면", "해서", "하고", "는데", "지만", "거나", "려고", "위해"
        ];
        return endings.Any(ending =>
            normalized.Equals(ending, StringComparison.Ordinal)
            || normalized.EndsWith($" {ending}", StringComparison.Ordinal)
            || (ContainsHangul(normalized) && normalized.EndsWith(ending, StringComparison.Ordinal)));
    }

    private static string PreferMoreCompleteText(string current, string incoming)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return incoming.Trim();
        }

        if (AreRelated(current, incoming) && CountWords(current) > CountWords(incoming))
        {
            return current.Trim();
        }

        return incoming.Trim();
    }

    private static string JoinFragments(string current, string continuation)
    {
        var left = current.Trim();
        var right = continuation.Trim();
        if (left.Length == 0)
        {
            return right;
        }

        if (right.Length == 0 || string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return left;
        }

        if (AreRelated(left, right))
        {
            return CountWords(right) > CountWords(left) ? right : left;
        }

        return $"{left.TrimEnd('.', ',', ';', ':')} {right}";
    }

    private static string ReplaceTrailingSegment(string aggregate, string previous, string replacement)
    {
        var trimmed = aggregate.TrimEnd();
        return trimmed.EndsWith(previous, StringComparison.Ordinal)
            ? trimmed[..^previous.Length] + replacement
            : aggregate;
    }

    private static bool AreRelated(string first, string second)
    {
        var firstWords = NormalizeWords(first);
        var secondWords = NormalizeWords(second);
        if (firstWords.Length == 0 || secondWords.Length == 0)
        {
            return false;
        }

        var shorter = firstWords.Length <= secondWords.Length ? firstWords : secondWords;
        var longer = firstWords.Length <= secondWords.Length ? secondWords : firstWords;
        if (shorter.Length <= 3)
        {
            return shorter.SequenceEqual(longer.Take(shorter.Length));
        }

        var common = LongestCommonSubsequence(firstWords, secondWords);
        return common >= Math.Max(3, (int)Math.Ceiling(shorter.Length * 0.7));
    }

    private static string[] NormalizeWords(string text)
    {
        var normalized = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormC).ToLowerInvariant())
        {
            normalized.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int CountWords(string text) => NormalizeWords(text).Length;

    private static int LongestCommonSubsequence(string[] first, string[] second)
    {
        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (var firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            for (var secondIndex = 1; secondIndex <= second.Length; secondIndex++)
            {
                current[secondIndex] = first[firstIndex - 1] == second[secondIndex - 1]
                    ? previous[secondIndex - 1] + 1
                    : Math.Max(previous[secondIndex], current[secondIndex - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[second.Length];
    }

    private static bool ContainsHangul(string text)
        => text.Any(character => character is >= '\uAC00' and <= '\uD7AF');
}

public enum AdaptiveHybridFinalizeMode
{
    AutomaticAfterSpeech,
    PhysicalMuteOnly
}
