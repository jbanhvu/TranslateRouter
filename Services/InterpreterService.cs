using System.Diagnostics;
using System.Threading.Channels;
using Grpc.Core;
using MeetingInterpreter.Models;
using NAudio.Wave;

namespace MeetingInterpreter.Services;

public sealed class InterpreterService : IDisposable
{
    private const int AdvancedSttWorkerCount = 2;
    private static readonly TimeSpan AdvancedMaximumUtteranceAge = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AdvancedMaximumAggregationDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AdvancedLanguageDecisionDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan AdvancedLanguageDecisionMaximumDelay = TimeSpan.FromMilliseconds(2200);
    private const double AdvancedLanguageSingleCandidateMinimumScore = 8.0;

    private readonly AudioService _audioService;
    private readonly GoogleTranslatePipeline _googlePipeline;
    private readonly GeminiApiClient _geminiClient;
    private readonly GeminiLiveSession _geminiLiveSession;
    private readonly GoogleStreamingSttService _googleStreamingSttService;
    private readonly AdaptiveHybridCoordinator _adaptiveHybridCoordinator;
    private readonly DeepgramSttService _deepgramSttService;
    private readonly InterpreterEngineFactory _engineFactory;
    private readonly VoiceActivityDetector _vad;
    private readonly AdvancedVoiceActivityDetector _advancedVad;
    private readonly DigitalSilenceDetector _digitalSilenceDetector = new();
    private readonly AppLogger _logger;
    private readonly object _stateSyncRoot = new();
    private Channel<AudioUtterance>? _utteranceChannel;
    private Channel<TranscriptUtterance>? _translationQueue;
    private Channel<TtsItem>? _ttsQueue;
    private Channel<PlaybackItem>? _playbackQueue;
    private Channel<AdvancedRecognitionOutcome>? _advancedRecognitionQueue;
    private CancellationTokenSource? _sessionCts;
    private Task? _workerTask;
    private Task? _translationWorkerTask;
    private Task? _ttsWorkerTask;
    private Task? _playbackWorkerTask;
    private Task? _streamingSupervisorTask;
    private Task? _advancedOrderingTask;
    private AudioDeviceInfo? _inputDevice;
    private AudioDeviceInfo? _output1Device;
    private AudioDeviceInfo? _output2Device;
    private string? _credentialPath;
    private bool _isRunning;
    private volatile bool _suppressMicrophoneProcessing;
    private long _utteranceSequence;
    private long _transcriptSequence;
    private int _pendingUtteranceCount;
    private int _pendingTtsCount;
    private int _pendingPlaybackCount;
    private string? _lastSignature;
    private DateTime _lastSignatureAt;
    private int _lastMicLevelUpdateTick;
    private readonly object _liveTextSyncRoot = new();
    private readonly System.Text.StringBuilder _liveOriginalText = new();
    private readonly System.Text.StringBuilder _liveTranslatedText = new();
    private DateTime _liveTurnStartedAt;
    private CancellationTokenSource? _livePlaybackSuppressCts;
    private readonly SemaphoreSlim _deepgramProcessingLock = new(1, 1);
    private readonly SemaphoreSlim _googleStreamingRestartLock = new(1, 1);
    private readonly PipelineRuntimeState _runtimeState = new();
    private readonly PipelineMetrics _metrics = new();
    private readonly object _lifecycleSyncRoot = new();
    private readonly Dictionary<long, UtteranceLifecycle> _utteranceLifecycles = new();
    private int _googleStreamingRestartAttempts;
    private int _googleStreamingOverloadCount;
    private DateTime _lastGoogleStreamingRestartAt;
    private DateTime _firstGoogleStreamingOverloadAt;
    private readonly object _advancedCircuitSyncRoot = new();
    private int _advancedConsecutiveTransientFailures;
    private DateTime _advancedCircuitOpenUntilUtc;
    private volatile bool _advancedSpeechActive;
    private int _advancedOutstandingRecognitionCount;
    private readonly object _advancedPreviewSyncRoot = new();
    private string _advancedAggregatePreviewText = string.Empty;
    private SupportedLanguage _advancedAggregatePreviewLanguage = SupportedLanguage.Unknown;
    private SupportedLanguage _advancedLivePreviewLanguage = SupportedLanguage.Unknown;
    private readonly Dictionary<SupportedLanguage, AdvancedLiveTranscriptCandidate> _advancedLiveCandidates = new();
    private DateTime _advancedLanguageDetectionStartedAtUtc;
    private bool _advancedAggregationActive;
    private int _advancedPreviewRestartAttempts;

    public InterpreterService(
        AudioService audioService,
        GoogleTranslatePipeline googlePipeline,
        GeminiApiClient geminiClient,
        GeminiLiveSession geminiLiveSession,
        GoogleStreamingSttService googleStreamingSttService,
        DeepgramSttService deepgramSttService,
        InterpreterEngineFactory engineFactory,
        VoiceActivityDetector vad,
        AppLogger logger,
        InterpreterSettings settings)
    {
        _audioService = audioService;
        _googlePipeline = googlePipeline;
        _geminiClient = geminiClient;
        _geminiLiveSession = geminiLiveSession;
        _googleStreamingSttService = googleStreamingSttService;
        _adaptiveHybridCoordinator = new AdaptiveHybridCoordinator(googlePipeline, logger);
        _deepgramSttService = deepgramSttService;
        _engineFactory = engineFactory;
        _vad = vad;
        _advancedVad = new AdvancedVoiceActivityDetector(settings);
        _logger = logger;
        Settings = settings;

        _audioService.AudioDataAvailable += OnAudioDataAvailable;
        _geminiLiveSession.AudioOutputReceived += OnGeminiLiveAudioOutputReceived;
        _geminiLiveSession.InputTranscriptionReceived += OnGeminiLiveInputTranscriptionReceived;
        _geminiLiveSession.OutputTranscriptionReceived += OnGeminiLiveOutputTranscriptionReceived;
        _geminiLiveSession.TurnCompleted += OnGeminiLiveTurnCompleted;
        _geminiLiveSession.StatusChanged += (_, message) => StatusChanged?.Invoke(this, message);
        _geminiLiveSession.Failed += OnGeminiLiveFailed;
        _googleStreamingSttService.InterimTranscriptReceived += OnGoogleStreamingInterimTranscriptReceived;
        _googleStreamingSttService.FinalTranscriptReceived += OnGoogleStreamingFinalTranscriptReceived;
        _googleStreamingSttService.StatusChanged += (_, message) => StatusChanged?.Invoke(this, message);
        _googleStreamingSttService.RecoverableFailure += OnGoogleStreamingRecoverableFailure;
        _googleStreamingSttService.FatalFailure += OnGoogleStreamingFatalFailure;
        _googleStreamingSttService.AudioQueueOverloaded += OnGoogleStreamingAudioQueueOverloaded;
        _adaptiveHybridCoordinator.PreviewChanged += OnAdaptivePreviewChanged;
        _adaptiveHybridCoordinator.TranscriptReady += OnAdaptiveTranscriptReady;
        _adaptiveHybridCoordinator.StatusChanged += OnAdaptiveStatusChanged;
        _deepgramSttService.InterimTranscriptReceived += OnDeepgramInterimTranscriptReceived;
        _deepgramSttService.FinalTranscriptReceived += OnDeepgramFinalTranscriptReceived;
        _deepgramSttService.StatusChanged += (_, message) => StatusChanged?.Invoke(this, message);
        _deepgramSttService.Failed += OnDeepgramFailed;
        _vad.SpeechStarted += (_, _) =>
        {
            _logger.Info("Phát hiện giọng nói.");
            SetState(InterpreterState.SpeechDetected, "Đã phát hiện giọng nói.");
        };
        _vad.SpeechEnded += (_, _) => _logger.Info("Kết thúc câu nói.");
    }

    public InterpreterSettings Settings { get; }

    public InterpreterState State { get; private set; } = InterpreterState.Idle;

    public event EventHandler<InterpreterStateChangedEventArgs>? StateChanged;

    public event EventHandler<int>? MicrophoneLevelChanged;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<InterpreterContentPreviewEventArgs>? ContentPreviewChanged;

    public event EventHandler<TranslationResult>? TranslationCompleted;

    public event EventHandler<UtteranceQueueStatusEventArgs>? QueueStatusChanged;

    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => _audioService.EnumerateInputDevices();

    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() => _audioService.EnumerateOutputDevices();

    public async Task InitializeGoogleAsync(string credentialPath, CancellationToken cancellationToken = default)
    {
        await _googlePipeline.InitializeAsync(credentialPath, cancellationToken).ConfigureAwait(false);
        _credentialPath = credentialPath;
        StatusChanged?.Invoke(this, "Đã kết nối Google Cloud.");
        _logger.Info("Đã kết nối Google Cloud.");
    }

    public async Task InitializeGeminiAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        Settings.Gemini.ApiKey = apiKey.Trim();
        await _geminiLiveSession.TestAsync(cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(this, "Đã kết nối Gemini.");
        _logger.Info("Đã kết nối Gemini.");
    }

    public async Task InitializeDeepgramAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        Settings.Deepgram.ApiKey = apiKey.Trim();
        await _deepgramSttService.TestAsync(cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke(this, "Đã kết nối Deepgram.");
        _logger.Info("Da ket noi Deepgram.");
    }

    public async Task StartSessionAsync(
        string? credentialPath,
        string? geminiApiKey,
        InterpreterEngineType engineType,
        AudioDeviceInfo inputDevice,
        AudioDeviceInfo output1Device,
        AudioDeviceInfo output2Device,
        CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("Phiên dịch đang chạy.");
        }

        Settings.EngineType = engineType;
        if (!string.IsNullOrWhiteSpace(geminiApiKey))
        {
            Settings.Gemini.ApiKey = geminiApiKey.Trim();
        }

        var currentDevices = ValidateDevices(inputDevice, output1Device, output2Device, credentialPath);
        inputDevice = currentDevices.Input;
        output1Device = currentDevices.Output1;
        output2Device = currentDevices.Output2;
        await EnsureSelectedEngineInitializedAsync(engineType, credentialPath, cancellationToken).ConfigureAwait(false);

        _credentialPath = credentialPath;
        _inputDevice = inputDevice;
        _output1Device = output1Device;
        _output2Device = output2Device;
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (engineType == InterpreterEngineType.Gemini25Pro)
        {
            await StartGeminiLiveSessionAsync(inputDevice, output1Device, _sessionCts.Token).ConfigureAwait(false);
            return;
        }

        if (engineType == InterpreterEngineType.GoogleCloudPipeline
            && Settings.SttEngineType == SttEngineType.DeepgramNova2)
        {
            await StartDeepgramSessionAsync(inputDevice, _sessionCts.Token).ConfigureAwait(false);
            return;
        }

        if (engineType == InterpreterEngineType.GoogleCloudStreamingPipeline)
        {
            await StartGoogleStreamingSessionAsync(inputDevice, _sessionCts.Token).ConfigureAwait(false);
            return;
        }

        if (engineType == InterpreterEngineType.GoogleCloudHybridPipeline)
        {
            await StartGoogleHybridSessionAsync(inputDevice, _sessionCts.Token).ConfigureAwait(false);
            return;
        }

        if (engineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline)
        {
            await StartGoogleAdvancedHybridSessionAsync(inputDevice, _sessionCts.Token).ConfigureAwait(false);
            return;
        }

        if (engineType is InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
        {
            await StartGoogleAdaptiveHybridSessionAsync(inputDevice, _sessionCts.Token).ConfigureAwait(false);
            return;
        }

        _utteranceChannel = Channel.CreateBounded<AudioUtterance>(new BoundedChannelOptions(Settings.MaxPendingUtterances)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _vad.Reset();
        _isRunning = true;
        _utteranceSequence = 0;
        _pendingUtteranceCount = 0;
        PublishQueueStatus(null);
        _suppressMicrophoneProcessing = false;
        _workerTask = Task.Run(() => ProcessQueueAsync(_sessionCts.Token), CancellationToken.None);
        _audioService.StartCapture(inputDevice);

        _logger.Info($"Bắt đầu phiên dịch bằng {GetEngineDisplayName(engineType)}.");
        _logger.Info($"Đã chọn loa phòng họp: {output1Device.Name}");
        _logger.Info($"Đã chọn tai nghe quản lý Hàn Quốc: {output2Device.Name}");
        SetState(InterpreterState.Listening, "Đang lắng nghe.");
    }

    public async Task StopSessionAsync()
    {
        if (!_isRunning && _sessionCts is null)
        {
            SetState(InterpreterState.Idle, "Sẵn sàng.");
            return;
        }

        var workerTask = _workerTask;
        var translationWorkerTask = _translationWorkerTask;
        var ttsWorkerTask = _ttsWorkerTask;
        var playbackWorkerTask = _playbackWorkerTask;
        var streamingSupervisorTask = _streamingSupervisorTask;
        var advancedOrderingTask = _advancedOrderingTask;
        _isRunning = false;
        _utteranceChannel?.Writer.TryComplete();
        _translationQueue?.Writer.TryComplete();
        _ttsQueue?.Writer.TryComplete();
        _playbackQueue?.Writer.TryComplete();
        _sessionCts?.Cancel();
        _adaptiveHybridCoordinator.Stop();
        await _geminiLiveSession.StopAsync().ConfigureAwait(false);
        await _googleStreamingSttService.StopAsync().ConfigureAwait(false);
        await _deepgramSttService.StopAsync().ConfigureAwait(false);
        _audioService.StopPcmStreamPlayback();
        _audioService.StopActivePlayback();
        _audioService.StopCapture();
        _vad.Reset();
        _advancedVad.Reset();
        _digitalSilenceDetector.Reset();

        if (workerTask is not null)
        {
            try
            {
                await workerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await AwaitWorkerTaskAsync(translationWorkerTask).ConfigureAwait(false);
        await AwaitWorkerTaskAsync(ttsWorkerTask).ConfigureAwait(false);
        await AwaitWorkerTaskAsync(playbackWorkerTask).ConfigureAwait(false);
        await AwaitWorkerTaskAsync(streamingSupervisorTask).ConfigureAwait(false);
        await AwaitWorkerTaskAsync(advancedOrderingTask).ConfigureAwait(false);

        _sessionCts?.Dispose();
        _sessionCts = null;
        _workerTask = null;
        _translationWorkerTask = null;
        _ttsWorkerTask = null;
        _playbackWorkerTask = null;
        _streamingSupervisorTask = null;
        _advancedOrderingTask = null;
        _utteranceChannel = null;
        _translationQueue = null;
        _ttsQueue = null;
        _playbackQueue = null;
        _advancedRecognitionQueue = null;
        _suppressMicrophoneProcessing = false;
        _livePlaybackSuppressCts?.Cancel();
        _livePlaybackSuppressCts?.Dispose();
        _livePlaybackSuppressCts = null;
        ResetLiveTurn();
        _pendingUtteranceCount = 0;
        _pendingTtsCount = 0;
        _pendingPlaybackCount = 0;
        _advancedSpeechActive = false;
        _advancedOutstandingRecognitionCount = 0;
        ResetAdvancedPreview();
        ResetAdvancedCircuit();
        UpdateRuntimeState(state =>
        {
            state.IsListening = false;
            state.IsSttConnected = false;
            state.IsReconnectingStt = false;
            state.IsTranslating = false;
            state.IsSynthesizing = false;
            state.IsPlaying = false;
        });
        PublishQueueStatus(null);
        _logger.Info("Dừng phiên dịch.");
        SetState(InterpreterState.Idle, "Sẵn sàng.");
    }

    public async Task RefreshDevicesAsync()
    {
        if (_isRunning)
        {
            _audioService.StopCapture();
            _vad.Reset();
            _advancedVad.Reset();

            if (_inputDevice is not null)
            {
                _audioService.StartCapture(_inputDevice);
            }
        }

        await Task.CompletedTask;
    }

    public async Task TestOutput1Async(AudioDeviceInfo outputDevice, CancellationToken cancellationToken)
    {
        var synthesisService = _engineFactory.GetSpeechSynthesisService(Settings.EngineType);
        if (CanUseSynthesisService(synthesisService))
        {
            var audio = await synthesisService.SynthesizeAsync("Đây là âm thanh kiểm tra loa phòng họp.", SupportedLanguage.Vietnamese, cancellationToken).ConfigureAwait(false);
            await _audioService.PlayAudioAsync(audio, outputDevice, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _audioService.PlayToneAsync(outputDevice, cancellationToken).ConfigureAwait(false);
    }

    public async Task TestOutput2Async(AudioDeviceInfo outputDevice, CancellationToken cancellationToken)
    {
        var synthesisService = _engineFactory.GetSpeechSynthesisService(Settings.EngineType);
        if (CanUseSynthesisService(synthesisService))
        {
            var audio = await synthesisService.SynthesizeAsync("헤드셋 테스트입니다.", SupportedLanguage.Korean, cancellationToken).ConfigureAwait(false);
            await _audioService.PlayAudioAsync(audio, outputDevice, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _audioService.PlayToneAsync(outputDevice, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _audioService.AudioDataAvailable -= OnAudioDataAvailable;
        _geminiLiveSession.AudioOutputReceived -= OnGeminiLiveAudioOutputReceived;
        _geminiLiveSession.InputTranscriptionReceived -= OnGeminiLiveInputTranscriptionReceived;
        _geminiLiveSession.OutputTranscriptionReceived -= OnGeminiLiveOutputTranscriptionReceived;
        _geminiLiveSession.TurnCompleted -= OnGeminiLiveTurnCompleted;
        _geminiLiveSession.Failed -= OnGeminiLiveFailed;
        _googleStreamingSttService.InterimTranscriptReceived -= OnGoogleStreamingInterimTranscriptReceived;
        _googleStreamingSttService.FinalTranscriptReceived -= OnGoogleStreamingFinalTranscriptReceived;
        _googleStreamingSttService.RecoverableFailure -= OnGoogleStreamingRecoverableFailure;
        _googleStreamingSttService.FatalFailure -= OnGoogleStreamingFatalFailure;
        _googleStreamingSttService.AudioQueueOverloaded -= OnGoogleStreamingAudioQueueOverloaded;
        _adaptiveHybridCoordinator.PreviewChanged -= OnAdaptivePreviewChanged;
        _adaptiveHybridCoordinator.TranscriptReady -= OnAdaptiveTranscriptReady;
        _adaptiveHybridCoordinator.StatusChanged -= OnAdaptiveStatusChanged;
        _deepgramSttService.InterimTranscriptReceived -= OnDeepgramInterimTranscriptReceived;
        _deepgramSttService.FinalTranscriptReceived -= OnDeepgramFinalTranscriptReceived;
        _deepgramSttService.Failed -= OnDeepgramFailed;
        _adaptiveHybridCoordinator.Stop();
        _geminiLiveSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _googleStreamingSttService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _deepgramSttService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _audioService.Dispose();
        _sessionCts?.Dispose();
        _livePlaybackSuppressCts?.Dispose();
        _deepgramProcessingLock.Dispose();
        _googleStreamingRestartLock.Dispose();
    }

    private async Task StartGeminiLiveSessionAsync(
        AudioDeviceInfo inputDevice,
        AudioDeviceInfo outputDevice,
        CancellationToken cancellationToken)
    {
        ResetLiveTurn();
        _vad.Reset();
        _isRunning = true;
        _pendingUtteranceCount = 0;
        _suppressMicrophoneProcessing = false;
        PublishQueueStatus(null);

        _audioService.StartPcmStreamPlayback(outputDevice, sampleRate: 24000);
        await _geminiLiveSession.StartAsync(cancellationToken).ConfigureAwait(false);
        _audioService.StartCapture(inputDevice);

        _logger.Info("[Gemini Live] Bat dau phien stream audio hai chieu.");
        SetState(InterpreterState.Listening, "Đang stream microphone tới Gemini Live API.");
    }

    private async Task StartDeepgramSessionAsync(
        AudioDeviceInfo inputDevice,
        CancellationToken cancellationToken)
    {
        _vad.Reset();
        _isRunning = true;
        _pendingUtteranceCount = 0;
        _suppressMicrophoneProcessing = false;
        PublishQueueStatus(null);

        await _deepgramSttService.StartAsync(cancellationToken).ConfigureAwait(false);
        _audioService.StartCapture(inputDevice);

        _logger.Info("[Deepgram STT] Bat dau stream microphone realtime.");
        SetState(InterpreterState.Listening, "Đang stream microphone tới Deepgram STT.");
    }

    private async Task StartGoogleStreamingSessionAsync(
        AudioDeviceInfo inputDevice,
        CancellationToken cancellationToken)
    {
        _translationQueue = Channel.CreateBounded<TranscriptUtterance>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _ttsQueue = Channel.CreateBounded<TtsItem>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _playbackQueue = Channel.CreateBounded<PlaybackItem>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _vad.Reset();
        _isRunning = true;
        _transcriptSequence = 0;
        _pendingUtteranceCount = 0;
        _pendingTtsCount = 0;
        _pendingPlaybackCount = 0;
        _suppressMicrophoneProcessing = false;
        UpdateRuntimeState(state =>
        {
            state.IsListening = true;
            state.IsSttConnected = false;
            state.IsReconnectingStt = false;
            state.IsTranslating = false;
            state.IsSynthesizing = false;
            state.IsPlaying = false;
        });
        PublishQueueStatus(null);

        await _googleStreamingSttService.StartAsync(cancellationToken).ConfigureAwait(false);
        _translationWorkerTask = Task.Run(() => ProcessTranslationQueueAsync(cancellationToken), CancellationToken.None);
        _ttsWorkerTask = Task.Run(() => ProcessTtsQueueAsync(cancellationToken), CancellationToken.None);
        _playbackWorkerTask = Task.Run(() => ProcessPlaybackQueueAsync(cancellationToken), CancellationToken.None);
        _streamingSupervisorTask = Task.Run(() => MonitorStreamingPipelineAsync(cancellationToken), CancellationToken.None);
        _audioService.StartCapture(inputDevice);

        _logger.Info("[Google Streaming STT] Bat dau engine Streaming Speech + Translate + TTS.");
        SetState(InterpreterState.Listening, "Đang nghe liên tục bằng Google Streaming Speech-to-Text.");
    }

    private async Task StartGoogleHybridSessionAsync(
        AudioDeviceInfo inputDevice,
        CancellationToken cancellationToken)
    {
        _utteranceChannel = Channel.CreateBounded<AudioUtterance>(new BoundedChannelOptions(Settings.MaxPendingUtterances)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _translationQueue = Channel.CreateBounded<TranscriptUtterance>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _ttsQueue = Channel.CreateBounded<TtsItem>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _playbackQueue = Channel.CreateBounded<PlaybackItem>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _vad.Reset();
        _isRunning = true;
        _utteranceSequence = 0;
        _transcriptSequence = 0;
        _pendingUtteranceCount = 0;
        _pendingTtsCount = 0;
        _pendingPlaybackCount = 0;
        _suppressMicrophoneProcessing = false;
        UpdateRuntimeState(state =>
        {
            state.IsListening = true;
            state.IsSttConnected = true;
            state.IsReconnectingStt = false;
            state.IsTranslating = false;
            state.IsSynthesizing = false;
            state.IsPlaying = false;
        });
        PublishQueueStatus(null);

        _workerTask = Task.Run(() => ProcessHybridRecognitionQueueAsync(cancellationToken), CancellationToken.None);
        _translationWorkerTask = Task.Run(() => ProcessTranslationQueueAsync(cancellationToken), CancellationToken.None);
        _ttsWorkerTask = Task.Run(() => ProcessTtsQueueAsync(cancellationToken), CancellationToken.None);
        _playbackWorkerTask = Task.Run(() => ProcessPlaybackQueueAsync(cancellationToken), CancellationToken.None);
        _streamingSupervisorTask = Task.Run(() => MonitorStreamingPipelineAsync(cancellationToken), CancellationToken.None);
        _audioService.StartCapture(inputDevice);

        _logger.Info("[Google Hybrid] Bat dau engine Hybrid Speech + Async Translate + TTS.");
        SetState(InterpreterState.Listening, "Dang nghe lien tuc bang Hybrid Speech + Queue.");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task StartGoogleAdvancedHybridSessionAsync(
        AudioDeviceInfo inputDevice,
        CancellationToken cancellationToken)
    {
        _utteranceChannel = Channel.CreateUnbounded<AudioUtterance>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _advancedRecognitionQueue = Channel.CreateUnbounded<AdvancedRecognitionOutcome>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _translationQueue = Channel.CreateBounded<TranscriptUtterance>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _ttsQueue = Channel.CreateBounded<TtsItem>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _playbackQueue = Channel.CreateBounded<PlaybackItem>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _advancedVad.Reset();
        _digitalSilenceDetector.Reset();
        ResetAdvancedCircuit();
        _isRunning = true;
        _utteranceSequence = 0;
        _transcriptSequence = 0;
        _pendingUtteranceCount = 0;
        _pendingTtsCount = 0;
        _pendingPlaybackCount = 0;
        _advancedSpeechActive = false;
        _advancedOutstandingRecognitionCount = 0;
        _advancedPreviewRestartAttempts = 0;
        ResetAdvancedPreview();
        _suppressMicrophoneProcessing = false;
        UpdateRuntimeState(state =>
        {
            state.IsListening = true;
            state.IsSttConnected = true;
            state.IsReconnectingStt = false;
            state.IsTranslating = false;
            state.IsSynthesizing = false;
            state.IsPlaying = false;
        });
        PublishQueueStatus(null);

        await _googleStreamingSttService.StartAsync(cancellationToken).ConfigureAwait(false);
        _workerTask = Task.Run(() => RunAdvancedRecognitionWorkersAsync(cancellationToken), CancellationToken.None);
        _advancedOrderingTask = Task.Run(() => ProcessAdvancedRecognitionResultsAsync(cancellationToken), CancellationToken.None);
        _translationWorkerTask = Task.Run(() => ProcessTranslationQueueAsync(cancellationToken), CancellationToken.None);
        _ttsWorkerTask = Task.Run(() => ProcessTtsQueueAsync(cancellationToken), CancellationToken.None);
        _playbackWorkerTask = Task.Run(() => ProcessPlaybackQueueAsync(cancellationToken), CancellationToken.None);
        _streamingSupervisorTask = Task.Run(() => MonitorStreamingPipelineAsync(cancellationToken), CancellationToken.None);
        _audioService.StartCapture(inputDevice);

        _logger.Info($"[Advanced Hybrid] Started with {AdvancedSttWorkerCount} STT workers, ordered delivery and resilient queues.");
        SetState(InterpreterState.Listening, "Đang nghe liên tục bằng Hybrid nâng cao.");
    }

    private async Task StartGoogleAdaptiveHybridSessionAsync(
        AudioDeviceInfo inputDevice,
        CancellationToken cancellationToken)
    {
        _translationQueue = Channel.CreateBounded<TranscriptUtterance>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _ttsQueue = Channel.CreateBounded<TtsItem>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _playbackQueue = Channel.CreateBounded<PlaybackItem>(new BoundedChannelOptions(Settings.MaxPendingTranslations)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _advancedVad.Reset();
        _isRunning = true;
        _transcriptSequence = 0;
        _pendingUtteranceCount = 0;
        _pendingTtsCount = 0;
        _pendingPlaybackCount = 0;
        _googleStreamingRestartAttempts = 0;
        _suppressMicrophoneProcessing = false;
        UpdateRuntimeState(state =>
        {
            state.IsListening = true;
            state.IsSttConnected = false;
            state.IsReconnectingStt = false;
            state.IsTranslating = false;
            state.IsSynthesizing = false;
            state.IsPlaying = false;
        });
        PublishQueueStatus(null);

        var physicalMuteMode = Settings.EngineType == InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline;
        _adaptiveHybridCoordinator.Start(
            cancellationToken,
            physicalMuteMode
                ? AdaptiveHybridFinalizeMode.PhysicalMuteOnly
                : AdaptiveHybridFinalizeMode.AutomaticAfterSpeech);
        await _googleStreamingSttService
            .StartAsync(cancellationToken, singleBilingualStream: true)
            .ConfigureAwait(false);
        _translationWorkerTask = Task.Run(() => ProcessTranslationQueueAsync(cancellationToken), CancellationToken.None);
        _ttsWorkerTask = Task.Run(() => ProcessTtsQueueAsync(cancellationToken), CancellationToken.None);
        _playbackWorkerTask = Task.Run(() => ProcessPlaybackQueueAsync(cancellationToken), CancellationToken.None);
        _streamingSupervisorTask = Task.Run(() => MonitorStreamingPipelineAsync(cancellationToken), CancellationToken.None);
        _audioService.StartCapture(inputDevice);

        _logger.Info(physicalMuteMode
            ? "[Physical Mute Hybrid] Started. Translation is committed by sustained digital silence."
            : "[Adaptive Hybrid] Started with one bilingual streaming STT and selective batch verification.");
        SetState(
            InterpreterState.Listening,
            physicalMuteMode
                ? "Micro đang hoạt động. Hệ thống chỉ hiển thị transcript và chờ tắt micro để dịch."
                : "Đang nghe bằng Hybrid thích ứng.");
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        var rms = VoiceActivityDetector.CalculateRms(e.Buffer, e.BytesRecorded);
        PublishMicrophoneLevel(rms);

        if (!_isRunning || _suppressMicrophoneProcessing)
        {
            return;
        }

        if (Settings.EngineType == InterpreterEngineType.Gemini25Pro)
        {
            var audioData = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, audioData, 0, e.BytesRecorded);
            _geminiLiveSession.EnqueueAudio(audioData);
            return;
        }

        if (Settings.EngineType == InterpreterEngineType.GoogleCloudStreamingPipeline)
        {
            var audioData = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, audioData, 0, e.BytesRecorded);
            _googleStreamingSttService.EnqueueAudio(audioData);
            return;
        }

        if (Settings.EngineType == InterpreterEngineType.GoogleCloudPipeline
            && Settings.SttEngineType == SttEngineType.DeepgramNova2)
        {
            var audioData = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, audioData, 0, e.BytesRecorded);
            _deepgramSttService.EnqueueAudio(audioData);
            return;
        }

        if (Settings.EngineType is InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
        {
            try
            {
                var streamingAudio = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, streamingAudio, 0, e.BytesRecorded);
                _googleStreamingSttService.EnqueueAudio(streamingAudio);

                if (Settings.EngineType == InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
                {
                    var transition = _digitalSilenceDetector.ProcessBuffer(e.Buffer, e.BytesRecorded);
                    if (transition == DigitalSilenceTransition.Muted)
                    {
                        _logger.Info("[Physical Mute Hybrid] Sustained digital silence detected. Committing transcript.");
                        _adaptiveHybridCoordinator.OnPhysicalMuteDetected();
                    }
                    else if (transition == DigitalSilenceTransition.Resumed)
                    {
                        _logger.Info("[Physical Mute Hybrid] PCM signal resumed.");
                        _adaptiveHybridCoordinator.OnPhysicalInputResumed();
                    }
                }

                var result = _advancedVad.ProcessBuffer(e.Buffer, e.BytesRecorded);
                if (result.SpeechStarted)
                {
                    _adaptiveHybridCoordinator.OnSpeechStarted();
                    SetState(InterpreterState.SpeechDetected, "Đang nhận dạng ngôn ngữ đầu vào...");
                }

                if (result.SpeechEnded)
                {
                    _adaptiveHybridCoordinator.OnSpeechEnded(result.UtteranceAudio);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[Adaptive Hybrid] Microphone processing failed.", ex);
                StatusChanged?.Invoke(this, "Xử lý âm thanh lỗi. Hybrid thích ứng vẫn tiếp tục lắng nghe.");
                _advancedVad.Reset();
            }

            return;
        }

        if (Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline)
        {
            try
            {
                var streamingAudio = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, streamingAudio, 0, e.BytesRecorded);
                _googleStreamingSttService.EnqueueAudio(streamingAudio);

                var result = _advancedVad.ProcessBuffer(e.Buffer, e.BytesRecorded);
                if (result.SpeechStarted)
                {
                    _advancedSpeechActive = true;
                    lock (_advancedPreviewSyncRoot)
                    {
                        if (!_advancedAggregationActive)
                        {
                            _advancedAggregatePreviewText = string.Empty;
                            ResetAdvancedLiveTranscriptLocked();
                        }
                    }
                    SetState(InterpreterState.SpeechDetected, "Đang nghe để xác định ngôn ngữ đầu vào...");
                }

                if (result.SpeechEnded)
                {
                    _advancedSpeechActive = false;
                }

                if (result.UtteranceAudio is not null && _utteranceChannel is not null)
                {
                    EnqueueUtterance(result.UtteranceAudio);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[Advanced Hybrid] Xử lý dữ liệu microphone thất bại.", ex);
                StatusChanged?.Invoke(this, "Xử lý âm thanh lỗi. Hybrid nâng cao vẫn tiếp tục lắng nghe.");
                _advancedVad.Reset();
            }

            return;
        }

        try
        {
            var result = _vad.ProcessBuffer(e.Buffer, e.BytesRecorded);
            if (result.UtteranceAudio is not null && _utteranceChannel is not null)
            {
                EnqueueUtterance(result.UtteranceAudio);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Xử lý dữ liệu microphone thất bại.", ex);
            SetState(InterpreterState.Error, "Thu âm từ microphone thất bại.");
        }
    }

    private void EnqueueUtterance(byte[] audioData)
    {
        if (_utteranceChannel is null)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _utteranceSequence);
        var utterance = new AudioUtterance
        {
            SequenceNumber = sequence,
            CapturedAt = DateTime.Now,
            AudioData = audioData,
            Duration = TimeSpan.FromMilliseconds(audioData.Length / 32.0)
        };

        _logger.Info($"[Utterance #{sequence}] Captured. Bytes={audioData.Length}; Duration={utterance.Duration.TotalSeconds:0.00}s");

        if (Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline)
        {
            SetUtteranceStage(sequence, UtteranceStage.Captured);
        }

        var isAdvancedHybrid = Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline;
        if (isAdvancedHybrid)
        {
            Interlocked.Increment(ref _advancedOutstandingRecognitionCount);
        }

        if (!_utteranceChannel.Writer.TryWrite(utterance))
        {
            if (isAdvancedHybrid)
            {
                Interlocked.Decrement(ref _advancedOutstandingRecognitionCount);
            }

            var warning = "Hàng đợi phiên dịch đã đầy. Câu nói mới chưa được đưa vào xử lý.";
            _logger.Error($"[Utterance #{sequence}] Queue full. MaxPending={Settings.MaxPendingUtterances}");
            StatusChanged?.Invoke(this, warning);
            return;
        }

        var pending = Interlocked.Increment(ref _pendingUtteranceCount);
        _logger.Info($"[Utterance #{sequence}] Queued. Pending={pending}");
        PublishQueueStatus(null);
    }

    private void OnGeminiLiveAudioOutputReceived(object? sender, byte[] audioData)
    {
        if (!_isRunning || Settings.EngineType != InterpreterEngineType.Gemini25Pro)
        {
            return;
        }

        SetState(InterpreterState.Playing, "Đang phát âm thanh từ Gemini Live API.");
        _suppressMicrophoneProcessing = true;
        _audioService.AddPcmPlaybackData(audioData);
        ReleaseLivePlaybackSuppressionLater();
    }

    private void OnGeminiLiveInputTranscriptionReceived(object? sender, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_liveTextSyncRoot)
        {
            if (_liveOriginalText.Length == 0)
            {
                _liveTurnStartedAt = DateTime.Now;
            }

            AppendTranscript(_liveOriginalText, text);
            ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Nội dung nói", _liveOriginalText.ToString()));
        }
    }

    private void OnGeminiLiveOutputTranscriptionReceived(object? sender, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_liveTextSyncRoot)
        {
            AppendTranscript(_liveTranslatedText, text);
            ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Nội dung dịch", _liveTranslatedText.ToString()));
        }
    }

    private void OnGeminiLiveTurnCompleted(object? sender, EventArgs e)
    {
        TranslationResult? result = null;
        lock (_liveTextSyncRoot)
        {
            var originalText = _liveOriginalText.ToString().Trim();
            var translatedText = _liveTranslatedText.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(originalText) || !string.IsNullOrWhiteSpace(translatedText))
            {
                var sourceLanguage = InferLanguageFromText(originalText);
                var targetLanguage = InferLanguageFromText(translatedText);
                result = new TranslationResult
                {
                    Timestamp = _liveTurnStartedAt == default ? DateTime.Now : _liveTurnStartedAt,
                    Engine = InterpreterEngineType.Gemini25Pro,
                    SourceLanguage = sourceLanguage,
                    TargetLanguage = targetLanguage,
                    OriginalText = originalText,
                    TranslatedText = translatedText,
                    Success = !string.IsNullOrWhiteSpace(translatedText),
                    TotalMilliseconds = (DateTime.Now - (_liveTurnStartedAt == default ? DateTime.Now : _liveTurnStartedAt)).TotalMilliseconds
                };
            }

            ResetLiveTurn();
        }

        if (result is not null)
        {
            TranslationCompleted?.Invoke(this, result);
        }

        if (_isRunning)
        {
            SetState(InterpreterState.Listening, "Đang stream microphone tới Gemini Live API.");
        }
    }

    private void OnGeminiLiveFailed(object? sender, Exception exception)
    {
        _logger.Error("[Gemini Live] Phien live gap loi.", exception);
        SetState(InterpreterState.Error, exception.Message);
    }

    private void OnDeepgramInterimTranscriptReceived(object? sender, StreamingTranscriptEventArgs args)
    {
        if (!_isRunning || Settings.SttEngineType != SttEngineType.DeepgramNova2)
        {
            return;
        }

        ContentPreviewChanged?.Invoke(
            this,
            new InterpreterContentPreviewEventArgs(
                "Nội dung nói",
                args.Text,
                isTranslation: false,
                isInterim: true,
                language: args.Language));
    }

    private void OnDeepgramFinalTranscriptReceived(object? sender, StreamingTranscriptEventArgs args)
    {
        if (!_isRunning || Settings.SttEngineType != SttEngineType.DeepgramNova2 || string.IsNullOrWhiteSpace(args.Text))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await ProcessDeepgramFinalTranscriptAsync(args, _sessionCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    private void OnDeepgramFailed(object? sender, Exception exception)
    {
        _logger.Error("[Deepgram STT] Phien realtime gap loi.", exception);
        SetState(InterpreterState.Error, exception.Message);
    }

    private void OnGoogleStreamingInterimTranscriptReceived(object? sender, StreamingTranscriptEventArgs args)
    {
        if (!_isRunning)
        {
            return;
        }

        if (Settings.EngineType is InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
        {
            _adaptiveHybridCoordinator.OnInterimTranscript(args);
            return;
        }

        if (Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline)
        {
            PublishAdvancedRealtimePreview(args, isFinal: false);
            return;
        }

        if (Settings.EngineType != InterpreterEngineType.GoogleCloudStreamingPipeline)
        {
            return;
        }

        UpdateRuntimeState(state =>
        {
            state.IsSttConnected = true;
            state.IsReconnectingStt = false;
            state.IsListening = true;
        });
        ContentPreviewChanged?.Invoke(
            this,
            new InterpreterContentPreviewEventArgs(
                "Đang nghe",
                args.Text,
                isTranslation: false,
                isInterim: true,
                language: ResolveGoogleStreamingSourceLanguage(args)));
    }

    private void OnGoogleStreamingFinalTranscriptReceived(object? sender, StreamingTranscriptEventArgs args)
    {
        if (_isRunning
            && Settings.EngineType is (InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
            && !string.IsNullOrWhiteSpace(args.Text))
        {
            _googleStreamingRestartAttempts = 0;
            _adaptiveHybridCoordinator.OnFinalTranscript(args);
            return;
        }

        if (_isRunning
            && Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
            && !string.IsNullOrWhiteSpace(args.Text))
        {
            _advancedPreviewRestartAttempts = 0;
            PublishAdvancedRealtimePreview(args, isFinal: true);
            return;
        }

        if (!_isRunning
            || Settings.EngineType != InterpreterEngineType.GoogleCloudStreamingPipeline
            || string.IsNullOrWhiteSpace(args.Text))
        {
            return;
        }

        UpdateRuntimeState(state =>
        {
            state.IsSttConnected = true;
            state.IsReconnectingStt = false;
            state.IsListening = true;
        });
        _googleStreamingOverloadCount = 0;
        _googleStreamingRestartAttempts = 0;
        EnqueueTranscriptUtterance(args);
    }

    private void OnAdaptivePreviewChanged(object? sender, InterpreterContentPreviewEventArgs args)
        => ContentPreviewChanged?.Invoke(this, args);

    private void OnAdaptiveStatusChanged(object? sender, string message)
        => StatusChanged?.Invoke(this, message);

    private void OnAdaptiveTranscriptReady(TranscriptUtterance utterance)
    {
        if (!_isRunning
            || Settings.EngineType is not (InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
            || _translationQueue is null)
        {
            return;
        }

        Interlocked.Increment(ref _metrics.TotalSttFinal);
        SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.SttFinal);
        ContentPreviewChanged?.Invoke(
            this,
            new InterpreterContentPreviewEventArgs(
                "Nội dung đã chốt",
                utterance.Text,
                isTranslation: false,
                isInterim: false,
                language: utterance.SourceLanguage));

        if (!_translationQueue.Writer.TryWrite(utterance))
        {
            SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.Failed, "Translation queue full");
            Interlocked.Increment(ref _metrics.TotalFailed);
            TranslationCompleted?.Invoke(this, new TranslationResult
            {
                Timestamp = utterance.CreatedAt,
                Engine = Settings.EngineType,
                SourceLanguage = utterance.SourceLanguage,
                TargetLanguage = utterance.TargetLanguage,
                OriginalText = utterance.Text,
                Confidence = utterance.Confidence,
                Success = false,
                ErrorMessage = "Hàng đợi dịch đã đầy"
            });
            StatusChanged?.Invoke(this, "Hệ thống đang xử lý chậm hơn tốc độ nói. Vui lòng chờ hàng đợi dịch.");
            return;
        }

        Interlocked.Increment(ref _pendingUtteranceCount);
        Interlocked.Increment(ref _metrics.TotalTranslationQueued);
        SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.QueuedForTranslation);
        _logger.Info($"[Adaptive Hybrid #{utterance.SequenceNumber}] Queued for translation: {utterance.Text}");
        PublishQueueStatus(null);
    }

    private void OnGoogleStreamingRecoverableFailure(object? sender, Exception exception)
    {
        _logger.Error("[Google Streaming STT] Loi tam thoi, dang tu ket noi lai.", exception);
        if (_isRunning && Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline)
        {
            StatusChanged?.Invoke(this, "Hiển thị chữ trực tiếp đang kết nối lại; chức năng gom câu và dịch vẫn tiếp tục.");
            return;
        }

        if (_isRunning && Settings.EngineType is InterpreterEngineType.GoogleCloudStreamingPipeline
            or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
        {
            UpdateRuntimeState(state =>
            {
                state.IsSttConnected = false;
                state.IsReconnectingStt = true;
            });
            SetState(InterpreterState.Listening, exception.Message);
        }
    }

    private void OnGoogleStreamingFatalFailure(object? sender, Exception exception)
    {
        _logger.Error("[Google Streaming STT] Loi khong the tu phuc hoi.", exception);
        if (_isRunning && Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline)
        {
            RequestAdvancedPreviewRestart(exception.Message);
            return;
        }

        RequestGoogleStreamingRestart($"Google Streaming STT gap loi nang: {exception.Message}");
    }

    private void OnGoogleStreamingAudioQueueOverloaded(object? sender, EventArgs args)
    {
        if (!_isRunning
            || Settings.EngineType is not (InterpreterEngineType.GoogleCloudStreamingPipeline
                or InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _firstGoogleStreamingOverloadAt > TimeSpan.FromSeconds(10))
        {
            _firstGoogleStreamingOverloadAt = now;
            _googleStreamingOverloadCount = 0;
        }

        _googleStreamingOverloadCount++;
        if (_googleStreamingOverloadCount >= 5)
        {
            if (Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline)
            {
                RequestAdvancedPreviewRestart("Luồng hiển thị chữ trực tiếp bị quá tải.");
            }
            else
            {
                RequestGoogleStreamingRestart("Google Streaming STT bi qua tai audio queue lien tuc.");
            }
        }
    }

    private void PublishAdvancedRealtimePreview(StreamingTranscriptEventArgs args, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(args.Text))
        {
            return;
        }

        var streamLanguage = args.StreamLanguage != SupportedLanguage.Unknown
            ? args.StreamLanguage
            : ResolveGoogleStreamingSourceLanguage(args);
        string displayText;
        SupportedLanguage displayLanguage;
        lock (_advancedPreviewSyncRoot)
        {
            if (!_advancedSpeechActive && !_advancedAggregationActive)
            {
                return;
            }

            var candidateLanguage = ResolveAdvancedPreviewCandidateLanguage(args, streamLanguage);
            if (candidateLanguage == SupportedLanguage.Unknown)
            {
                return;
            }

            if (!_advancedLiveCandidates.TryGetValue(candidateLanguage, out var candidate))
            {
                candidate = new AdvancedLiveTranscriptCandidate(candidateLanguage);
                _advancedLiveCandidates[candidateLanguage] = candidate;
            }

            candidate.Update(args.Text.Trim(), args.Language, args.Confidence, isFinal);
            if (_advancedLivePreviewLanguage == SupportedLanguage.Unknown)
            {
                _advancedLivePreviewLanguage = TrySelectAdvancedPreviewLanguageLocked(DateTime.UtcNow);
            }

            if (_advancedLivePreviewLanguage == SupportedLanguage.Unknown
                || candidateLanguage != _advancedLivePreviewLanguage)
            {
                return;
            }

            displayText = ComposeAdvancedLiveTranscriptLocked();
            displayLanguage = _advancedLivePreviewLanguage;
        }

        UpdateRuntimeState(state =>
        {
            state.IsSttConnected = true;
            state.IsReconnectingStt = false;
            state.IsListening = true;
        });
        ContentPreviewChanged?.Invoke(
            this,
            new InterpreterContentPreviewEventArgs(
                "Đang nghe trực tiếp",
                displayText,
                isTranslation: false,
                isInterim: true,
                language: displayLanguage));
    }

    private void SetAdvancedAggregatePreview(string text, bool isActive, SupportedLanguage language)
    {
        lock (_advancedPreviewSyncRoot)
        {
            _advancedAggregatePreviewText = text.Trim();
            _advancedAggregatePreviewLanguage = language;
            _advancedAggregationActive = isActive;
        }
    }

    private void ResetAdvancedPreview()
    {
        lock (_advancedPreviewSyncRoot)
        {
            _advancedAggregatePreviewText = string.Empty;
            _advancedAggregatePreviewLanguage = SupportedLanguage.Unknown;
            ResetAdvancedLiveTranscriptLocked();
            _advancedAggregationActive = false;
        }
    }

    private string ComposeAdvancedLiveTranscriptLocked()
        => _advancedLivePreviewLanguage != SupportedLanguage.Unknown
            && _advancedLiveCandidates.TryGetValue(_advancedLivePreviewLanguage, out var candidate)
                ? candidate.Text
                : string.Empty;

    private void ResetAdvancedLiveTranscriptLocked()
    {
        _advancedLivePreviewLanguage = SupportedLanguage.Unknown;
        _advancedLiveCandidates.Clear();
        _advancedLanguageDetectionStartedAtUtc = DateTime.UtcNow;
    }

    private SupportedLanguage TrySelectAdvancedPreviewLanguageLocked(DateTime nowUtc)
    {
        var elapsed = nowUtc - _advancedLanguageDetectionStartedAtUtc;
        if (elapsed < AdvancedLanguageDecisionDelay)
        {
            return SupportedLanguage.Unknown;
        }

        _advancedLiveCandidates.TryGetValue(SupportedLanguage.Vietnamese, out var vietnamese);
        _advancedLiveCandidates.TryGetValue(SupportedLanguage.Korean, out var korean);
        var vietnameseReady = vietnamese?.HasEnoughEvidence(elapsed >= AdvancedLanguageDecisionMaximumDelay) == true;
        var koreanReady = korean?.HasEnoughEvidence(elapsed >= AdvancedLanguageDecisionMaximumDelay) == true;
        if (!vietnameseReady && !koreanReady)
        {
            return SupportedLanguage.Unknown;
        }

        if (vietnameseReady && !koreanReady)
        {
            var candidate = vietnamese!;
            return elapsed >= AdvancedLanguageDecisionMaximumDelay
                    ? LockAdvancedPreviewLanguageLocked(candidate)
                    : SupportedLanguage.Unknown;
        }

        if (koreanReady && !vietnameseReady)
        {
            var candidate = korean!;
            return elapsed >= AdvancedLanguageDecisionMaximumDelay
                || candidate.GetLanguageScore() >= AdvancedLanguageSingleCandidateMinimumScore
                    ? LockAdvancedPreviewLanguageLocked(candidate)
                    : SupportedLanguage.Unknown;
        }

        var vietnameseScore = vietnamese!.GetLanguageScore();
        var koreanScore = korean!.GetLanguageScore();
        if (elapsed < AdvancedLanguageDecisionMaximumDelay
            && Math.Abs(vietnameseScore - koreanScore) < 2.0)
        {
            return SupportedLanguage.Unknown;
        }

        return LockAdvancedPreviewLanguageLocked(
            koreanScore > vietnameseScore ? korean : vietnamese);
    }

    private SupportedLanguage LockAdvancedPreviewLanguageLocked(AdvancedLiveTranscriptCandidate candidate)
    {
        _logger.Info(
            $"[Advanced Hybrid Preview] Locked language={candidate.Language}; " +
            $"score={candidate.GetLanguageScore():0.0}; words={candidate.WordCount}; text={candidate.Text}");
        return candidate.Language;
    }

    private static SupportedLanguage ResolveAdvancedPreviewCandidateLanguage(
        StreamingTranscriptEventArgs args,
        SupportedLanguage streamLanguage)
    {
        if (ContainsHangul(args.Text))
        {
            return SupportedLanguage.Korean;
        }

        if (args.Language == SupportedLanguage.Vietnamese
            || streamLanguage == SupportedLanguage.Vietnamese)
        {
            return SupportedLanguage.Vietnamese;
        }

        return SupportedLanguage.Unknown;
    }

    private static string PreferMoreCompleteAdvancedText(string currentText, string incomingText)
    {
        var current = currentText.Trim();
        var incoming = incomingText.Trim();
        if (current.Length == 0 || incoming.Length == 0)
        {
            return incoming;
        }

        if (AreAdvancedTranscriptsRelated(current, incoming)
            && CountAdvancedTranscriptWords(current) > CountAdvancedTranscriptWords(incoming))
        {
            return current;
        }

        return incoming;
    }

    private static string MergeAdvancedStreamingText(string finalizedText, string currentText)
    {
        var finalized = finalizedText.Trim();
        var current = currentText.Trim();
        if (finalized.Length == 0)
        {
            return current;
        }

        if (current.Length == 0)
        {
            return finalized;
        }

        if (AreAdvancedTranscriptsRelated(finalized, current))
        {
            return CountAdvancedTranscriptWords(current) >= CountAdvancedTranscriptWords(finalized)
                ? current
                : finalized;
        }

        return $"{finalized} {current}";
    }

    private static bool AreAdvancedTranscriptsRelated(string first, string second)
    {
        var firstWords = NormalizeAdvancedTranscriptWords(first);
        var secondWords = NormalizeAdvancedTranscriptWords(second);
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

        var commonWordCount = GetAdvancedLongestCommonSubsequenceLength(firstWords, secondWords);
        var sameOpening = firstWords.Take(2).SequenceEqual(secondWords.Take(2));
        var shorterIsContained = commonWordCount == shorter.Length;
        return (sameOpening || shorterIsContained)
            && commonWordCount >= Math.Max(3, (int)Math.Ceiling(shorter.Length * 0.7));
    }

    private static string[] NormalizeAdvancedTranscriptWords(string text)
    {
        var normalized = new System.Text.StringBuilder(text.Length);
        foreach (var character in text.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant())
        {
            normalized.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return normalized
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int CountAdvancedTranscriptWords(string text)
        => NormalizeAdvancedTranscriptWords(text).Length;

    private static int GetAdvancedLongestCommonSubsequenceLength(string[] first, string[] second)
    {
        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (var firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            for (var secondIndex = 1; secondIndex <= second.Length; secondIndex++)
            {
                current[secondIndex] = string.Equals(first[firstIndex - 1], second[secondIndex - 1], StringComparison.Ordinal)
                    ? previous[secondIndex - 1] + 1
                    : Math.Max(previous[secondIndex], current[secondIndex - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[second.Length];
    }

    private void RequestAdvancedPreviewRestart(string reason)
    {
        if (!_isRunning || Settings.EngineType != InterpreterEngineType.GoogleCloudAdvancedHybridPipeline)
        {
            return;
        }

        _ = Task.Run(
            async () => await RestartAdvancedPreviewAsync(reason, _sessionCts?.Token ?? CancellationToken.None).ConfigureAwait(false),
            CancellationToken.None);
    }

    private async Task RestartAdvancedPreviewAsync(string reason, CancellationToken cancellationToken)
    {
        if (!_googleStreamingRestartLock.Wait(0))
        {
            return;
        }

        try
        {
            const int maxAttempts = 3;
            while (_isRunning
                && Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                && !cancellationToken.IsCancellationRequested)
            {
                _advancedPreviewRestartAttempts++;
                if (_advancedPreviewRestartAttempts > maxAttempts)
                {
                    StatusChanged?.Invoke(this, "Không thể khôi phục chữ trực tiếp. Chức năng gom câu và dịch vẫn hoạt động.");
                    return;
                }

                try
                {
                    _logger.Error($"[Advanced Hybrid Preview] Restart attempt {_advancedPreviewRestartAttempts}. Reason={reason}");
                    await _googleStreamingSttService.StopAsync().ConfigureAwait(false);
                    await Task.Delay(500 * _advancedPreviewRestartAttempts, cancellationToken).ConfigureAwait(false);
                    await _googleStreamingSttService.StartAsync(cancellationToken).ConfigureAwait(false);
                    _googleStreamingOverloadCount = 0;
                    StatusChanged?.Invoke(this, "Đã khôi phục hiển thị chữ trực tiếp.");
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Error("[Advanced Hybrid Preview] Restart failed.", ex);
                }
            }
        }
        finally
        {
            _googleStreamingRestartLock.Release();
        }
    }

    private void RequestGoogleStreamingRestart(string reason)
    {
        if (!_isRunning || Settings.EngineType is not (InterpreterEngineType.GoogleCloudStreamingPipeline
            or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline))
        {
            return;
        }

        _ = Task.Run(
            async () => await RestartGoogleStreamingAsync(reason, _sessionCts?.Token ?? CancellationToken.None).ConfigureAwait(false),
            CancellationToken.None);
    }

    private async Task RestartGoogleStreamingAsync(string reason, CancellationToken cancellationToken)
    {
        if (!_googleStreamingRestartLock.Wait(0))
        {
            _logger.Info($"[Google Streaming STT] Restart already running. Reason={reason}");
            return;
        }

        try
        {
            if (!_isRunning
                || Settings.EngineType is not (InterpreterEngineType.GoogleCloudStreamingPipeline
                    or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                    or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
                || _inputDevice is null
                || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (DateTime.UtcNow - _lastGoogleStreamingRestartAt > TimeSpan.FromMinutes(2))
            {
                _googleStreamingRestartAttempts = 0;
            }

            const int maxRestartAttempts = 5;
            while (_isRunning
                && (Settings.EngineType is InterpreterEngineType.GoogleCloudStreamingPipeline
                    or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                    or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
                && !cancellationToken.IsCancellationRequested)
            {
                _googleStreamingRestartAttempts++;
                _lastGoogleStreamingRestartAt = DateTime.UtcNow;

                if (_googleStreamingRestartAttempts > maxRestartAttempts)
                {
                    UpdateRuntimeState(state =>
                    {
                        state.IsListening = false;
                        state.IsSttConnected = false;
                        state.IsReconnectingStt = false;
                    });
                    SetState(InterpreterState.Error, "Google Streaming STT loi lien tuc, da thu khoi dong lai nhieu lan nhung chua thanh cong. Vui long kiem tra Internet, Google API va thiet bi microphone.");
                    return;
                }

                try
                {
                    _logger.Error($"[Google Streaming STT] Restart requested. Attempt={_googleStreamingRestartAttempts}. Reason={reason}");
                    UpdateRuntimeState(state =>
                    {
                        state.IsListening = false;
                        state.IsSttConnected = false;
                        state.IsReconnectingStt = true;
                    });
                    SetState(InterpreterState.Listening, $"Google Streaming gap loi, dang tu khoi dong lai lan {_googleStreamingRestartAttempts}...");

                    _suppressMicrophoneProcessing = true;
                    _audioService.StopCapture();
                    MicrophoneLevelChanged?.Invoke(this, 0);
                    await _googleStreamingSttService.StopAsync().ConfigureAwait(false);

                    var delayMs = Math.Min(500 * _googleStreamingRestartAttempts, 3000);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

                    if (Settings.EngineType is InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                        or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
                    {
                        _advancedVad.Reset();
                        _digitalSilenceDetector.Reset();
                        _adaptiveHybridCoordinator.Stop();
                        _adaptiveHybridCoordinator.Start(
                            cancellationToken,
                            Settings.EngineType == InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline
                                ? AdaptiveHybridFinalizeMode.PhysicalMuteOnly
                                : AdaptiveHybridFinalizeMode.AutomaticAfterSpeech);
                    }
                    else
                    {
                        _vad.Reset();
                    }
                    await _googleStreamingSttService
                        .StartAsync(
                            cancellationToken,
                            singleBilingualStream: Settings.EngineType is InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                                or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
                        .ConfigureAwait(false);
                    _audioService.StartCapture(_inputDevice);
                    _suppressMicrophoneProcessing = false;
                    _googleStreamingOverloadCount = 0;

                    UpdateRuntimeState(state =>
                    {
                        state.IsListening = true;
                        state.IsSttConnected = true;
                        state.IsReconnectingStt = false;
                    });
                    SetState(InterpreterState.Listening, "Google Streaming da khoi dong lai, dang tiep tuc nghe.");
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Error($"[Google Streaming STT] Restart attempt {_googleStreamingRestartAttempts} failed.", ex);
                    _suppressMicrophoneProcessing = false;
                    try
                    {
                        await Task.Delay(Math.Min(1000 * _googleStreamingRestartAttempts, 5000), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            _suppressMicrophoneProcessing = false;
            _googleStreamingRestartLock.Release();
        }
    }

    private void EnqueueTranscriptUtterance(StreamingTranscriptEventArgs args)
    {
        if (_translationQueue is null)
        {
            var missingQueueSequence = Interlocked.Increment(ref _transcriptSequence);
            _logger.Error($"[Streaming #{missingQueueSequence}] Translation queue is not available. Text={args.Text}");
            TranslationCompleted?.Invoke(this, new TranslationResult
            {
                Timestamp = DateTime.Now,
                Engine = InterpreterEngineType.GoogleCloudStreamingPipeline,
                OriginalText = args.Text.Trim(),
                SourceLanguage = args.Language,
                TargetLanguage = LanguageHelper.GetTargetLanguage(args.Language),
                Success = false,
                ErrorMessage = "Translation queue chưa sẵn sàng"
            });
            return;
        }

        var sourceLanguage = ResolveGoogleStreamingSourceLanguage(args);

        var targetLanguage = LanguageHelper.GetTargetLanguage(sourceLanguage);
        if (targetLanguage == SupportedLanguage.Unknown)
        {
            var skippedSequence = Interlocked.Increment(ref _transcriptSequence);
            _logger.Error($"[Streaming #{skippedSequence}] Language unknown. RawLanguageCode={args.RawLanguageCode}; Text={args.Text}");
            StatusChanged?.Invoke(this, "Không xác định được ngôn ngữ câu vừa nói. Bỏ qua câu này và tiếp tục nghe.");
            TranslationCompleted?.Invoke(this, new TranslationResult
            {
                Timestamp = DateTime.Now,
                Engine = InterpreterEngineType.GoogleCloudStreamingPipeline,
                SourceLanguage = SupportedLanguage.Unknown,
                TargetLanguage = SupportedLanguage.Unknown,
                OriginalText = args.Text.Trim(),
                Confidence = args.Confidence,
                Success = false,
                ErrorMessage = "Không xác định ngôn ngữ"
            });
            return;
        }

        var sequence = Interlocked.Increment(ref _transcriptSequence);
        Interlocked.Increment(ref _metrics.TotalSttFinal);
        SetUtteranceStage(sequence, UtteranceStage.SttFinal);
        var utterance = new TranscriptUtterance
        {
            SequenceNumber = sequence,
            CreatedAt = DateTime.Now,
            Text = args.Text.Trim(),
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            Confidence = args.Confidence
        };

        _logger.Info($"[Streaming #{sequence}] STT Final: {utterance.Text}");
        if (args.Language != sourceLanguage && sourceLanguage != SupportedLanguage.Unknown)
        {
            _logger.Info($"[Streaming #{sequence}] Language resolved from transcript text. RawLanguageCode={args.RawLanguageCode}; Google={args.Language}; Source={sourceLanguage}");
        }

        _logger.Info($"[Streaming #{sequence}] Language Detected. RawLanguageCode={args.RawLanguageCode}; Source={utterance.SourceLanguage}; Target={utterance.TargetLanguage}");
        ContentPreviewChanged?.Invoke(
            this,
            new InterpreterContentPreviewEventArgs("Nội dung nói", utterance.Text, false, false, utterance.SourceLanguage));

        if (!_translationQueue.Writer.TryWrite(utterance))
        {
            var warning = $"Hệ thống đang xử lý chậm hơn tốc độ nói. Đang chờ: {Volatile.Read(ref _pendingUtteranceCount)} câu.";
            _logger.Error($"[Streaming #{sequence}] Translation queue full. MaxPending={Settings.MaxPendingTranslations}");
            SetUtteranceStage(sequence, UtteranceStage.Failed, "Translation queue full");
            Interlocked.Increment(ref _metrics.TotalFailed);
            TranslationCompleted?.Invoke(this, new TranslationResult
            {
                Timestamp = utterance.CreatedAt,
                Engine = InterpreterEngineType.GoogleCloudStreamingPipeline,
                SourceLanguage = utterance.SourceLanguage,
                TargetLanguage = utterance.TargetLanguage,
                OriginalText = utterance.Text,
                Confidence = utterance.Confidence,
                Success = false,
                ErrorMessage = "Hàng đợi dịch đã đầy"
            });
            StatusChanged?.Invoke(this, warning);
            return;
        }

        Interlocked.Increment(ref _pendingUtteranceCount);
        Interlocked.Increment(ref _metrics.TotalTranslationQueued);
        SetUtteranceStage(sequence, UtteranceStage.QueuedForTranslation);
        _logger.Info($"[Streaming #{sequence}] QueuedForTranslation. Pending={Volatile.Read(ref _pendingUtteranceCount)}");
        PublishQueueStatus(null);
    }

    private async Task ProcessDeepgramFinalTranscriptAsync(StreamingTranscriptEventArgs args, CancellationToken cancellationToken)
    {
        await _deepgramProcessingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var totalStopwatch = Stopwatch.StartNew();
        var result = new TranslationResult
        {
            Timestamp = DateTime.Now,
            Engine = InterpreterEngineType.GoogleCloudPipeline,
            OriginalText = args.Text.Trim(),
            SourceLanguage = args.Language == SupportedLanguage.Unknown
                ? InferLanguageFromText(args.Text)
                : args.Language,
            Confidence = args.Confidence
        };

        try
        {
            if (result.SourceLanguage == SupportedLanguage.Unknown)
            {
                totalStopwatch.Stop();
                result.Success = false;
                result.TotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
                result.ErrorMessage = "Không xác định ngôn ngữ";
                TranslationCompleted?.Invoke(this, result);
                _logger.Error($"[Deepgram STT] Khong xac dinh ngon ngu. RawLanguageCode={args.RawLanguageCode}; Text={args.Text}");
                SetState(InterpreterState.Listening, "Không xác định được ngôn ngữ câu vừa nói. Bỏ qua câu này và tiếp tục nghe.");
                return;
            }

            result.TargetLanguage = LanguageHelper.GetTargetLanguage(result.SourceLanguage);
            ContentPreviewChanged?.Invoke(
                this,
                new InterpreterContentPreviewEventArgs("Nội dung nói", result.OriginalText, false, false, result.SourceLanguage));

            if (IsDuplicate(result.OriginalText, result.SourceLanguage))
            {
                return;
            }

            if (Settings.RecognitionOnlyMode)
            {
                totalStopwatch.Stop();
                result.Success = true;
                result.TotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
                TranslationCompleted?.Invoke(this, result);
                return;
            }

            SetState(InterpreterState.Translating, $"Đang dịch {GetLanguageDisplayName(result.SourceLanguage)}...");
            var stage = Stopwatch.StartNew();
            result.TranslatedText = await _googlePipeline.TranslateTextAsync(
                result.OriginalText,
                result.SourceLanguage,
                result.TargetLanguage,
                cancellationToken).ConfigureAwait(false);
            stage.Stop();
            result.TranslationMilliseconds = stage.Elapsed.TotalMilliseconds;
            ContentPreviewChanged?.Invoke(
                this,
                new InterpreterContentPreviewEventArgs("Nội dung dịch", result.TranslatedText, true, false, result.TargetLanguage));

            SetState(InterpreterState.Synthesizing, $"Đang tạo giọng nói {GetLanguageDisplayName(result.TargetLanguage)}...");
            stage.Restart();
            var ttsAudio = await _engineFactory
                .GetSpeechSynthesisService(InterpreterEngineType.GoogleCloudPipeline)
                .SynthesizeAsync(result.TranslatedText, result.TargetLanguage, cancellationToken)
                .ConfigureAwait(false);
            stage.Stop();
            result.SynthesisMilliseconds = stage.Elapsed.TotalMilliseconds;

            SetState(InterpreterState.Playing, GetPlaybackStatus(result.TargetLanguage));
            await PlayTranslatedAudioAsync(ttsAudio, result.TargetLanguage, cancellationToken).ConfigureAwait(false);

            totalStopwatch.Stop();
            result.TotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
            result.Success = true;
            TranslationCompleted?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            result.TotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
            result.Success = false;
            result.ErrorMessage = "Có lỗi";
            TranslationCompleted?.Invoke(this, result);
            _logger.Error("[Deepgram STT] Xu ly transcript final that bai.", ex);
            SetState(InterpreterState.Error, ToUserMessage(ex));
        }
        finally
        {
            _deepgramProcessingLock.Release();
            if (_isRunning && State != InterpreterState.Error)
            {
                SetState(InterpreterState.Listening, "Đang stream microphone tới Deepgram STT.");
            }
        }
    }

    private void ReleaseLivePlaybackSuppressionLater()
    {
        _livePlaybackSuppressCts?.Cancel();
        _livePlaybackSuppressCts?.Dispose();
        _livePlaybackSuppressCts = new CancellationTokenSource();
        var token = _livePlaybackSuppressCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Settings.PostPlaybackSilenceMs + 250, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested && _isRunning && Settings.EngineType == InterpreterEngineType.Gemini25Pro)
                {
                    _suppressMicrophoneProcessing = false;
                    SetState(InterpreterState.Listening, "Đang stream microphone tới Gemini Live API.");
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private void ResetLiveTurn()
    {
        _liveOriginalText.Clear();
        _liveTranslatedText.Clear();
        _liveTurnStartedAt = default;
    }

    private static void AppendTranscript(System.Text.StringBuilder builder, string text)
    {
        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
        {
            builder.Append(' ');
        }

        builder.Append(text.Trim());
    }

    private void DecrementPendingUtterances()
    {
        while (true)
        {
            var current = Volatile.Read(ref _pendingUtteranceCount);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _pendingUtteranceCount, current - 1, current) == current)
            {
                return;
            }
        }
    }

    private void DecrementPendingPlaybackItems()
    {
        while (true)
        {
            var current = Volatile.Read(ref _pendingPlaybackCount);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _pendingPlaybackCount, current - 1, current) == current)
            {
                return;
            }
        }
    }

    private void DecrementPendingTtsItems()
    {
        while (true)
        {
            var current = Volatile.Read(ref _pendingTtsCount);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _pendingTtsCount, current - 1, current) == current)
            {
                return;
            }
        }
    }

    private static async Task AwaitWorkerTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MonitorStreamingPipelineAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                if (!_isRunning || !IsQueuedGooglePipeline(Settings.EngineType))
                {
                    continue;
                }

                if (Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline)
                {
                    RestartWorkerIfNeeded(
                        ref _workerTask,
                        () => RunAdvancedRecognitionWorkersAsync(cancellationToken),
                        "Advanced Hybrid STT");
                }
                else if (Settings.EngineType == InterpreterEngineType.GoogleCloudHybridPipeline)
                {
                    RestartWorkerIfNeeded(
                        ref _workerTask,
                        () => ProcessHybridRecognitionQueueAsync(cancellationToken),
                        "Hybrid STT");
                }

                RestartWorkerIfNeeded(
                    ref _translationWorkerTask,
                    () => ProcessTranslationQueueAsync(cancellationToken),
                    "Translation");
                RestartWorkerIfNeeded(
                    ref _ttsWorkerTask,
                    () => ProcessTtsQueueAsync(cancellationToken),
                    "TTS");
                RestartWorkerIfNeeded(
                    ref _playbackWorkerTask,
                    () => ProcessPlaybackQueueAsync(cancellationToken),
                    "Playback");

                LogStuckQueuedTranslations();
                PublishQueueStatus(null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("[Streaming Supervisor] Watchdog failed.", ex);
            }
        }
    }

    private void RestartWorkerIfNeeded(ref Task? workerTask, Func<Task> workerFactory, string workerName)
    {
        if (workerTask is null || !workerTask.IsCompleted)
        {
            return;
        }

        _logger.Error($"[Streaming Supervisor] {workerName} worker stopped unexpectedly. Status={workerTask.Status}");
        StatusChanged?.Invoke(this, $"{workerName} worker đã dừng bất thường. Đang tự khởi động lại...");
        workerTask = Task.Run(workerFactory, CancellationToken.None);
    }

    private void SetUtteranceStage(long sequenceNumber, UtteranceStage stage, string? error = null)
    {
        lock (_lifecycleSyncRoot)
        {
            if (!_utteranceLifecycles.TryGetValue(sequenceNumber, out var lifecycle))
            {
                lifecycle = new UtteranceLifecycle
                {
                    SequenceNumber = sequenceNumber
                };
                _utteranceLifecycles[sequenceNumber] = lifecycle;
            }

            lifecycle.Stage = stage;
            lifecycle.UpdatedAt = DateTime.Now;
            if (stage == UtteranceStage.QueuedForTranslation)
            {
                lifecycle.QueuedForTranslationAt = lifecycle.UpdatedAt;
            }
            else if (stage == UtteranceStage.Translating)
            {
                lifecycle.ProcessingStartedAt = lifecycle.UpdatedAt;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                lifecycle.LastError = error;
            }

            if (_utteranceLifecycles.Count > 250)
            {
                foreach (var key in _utteranceLifecycles
                    .OrderBy(item => item.Value.UpdatedAt)
                    .Take(_utteranceLifecycles.Count - 250)
                    .Select(item => item.Key)
                    .ToList())
                {
                    _utteranceLifecycles.Remove(key);
                }
            }
        }
    }

    private void LogStuckQueuedTranslations()
    {
        List<UtteranceLifecycle> stuckItems;
        lock (_lifecycleSyncRoot)
        {
            var cutoff = DateTime.Now - TimeSpan.FromSeconds(15);
            stuckItems = _utteranceLifecycles.Values
                .Where(item => item.Stage == UtteranceStage.QueuedForTranslation
                    && item.QueuedForTranslationAt.HasValue
                    && item.QueuedForTranslationAt.Value < cutoff)
                .ToList();
        }

        foreach (var item in stuckItems)
        {
            _logger.Error($"[Streaming #{item.SequenceNumber}] Câu đang bị kẹt tại Translation Queue.");
        }
    }

    private async Task<T> RunWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        int retryCount,
        string operationName,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Clamp(retryCount, 0, 2) + 1;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                return await operation(timeoutCts.Token).WaitAsync(timeout, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (attempt < attempts)
            {
                var delay = TimeSpan.FromMilliseconds(400 * attempt);
                _logger.Error($"[Streaming] {operationName} timeout. Retry {attempt}/{attempts - 1} sau {delay.TotalMilliseconds:0}ms.", ex);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < attempts && IsTransientGoogleError(ex))
            {
                var delay = TimeSpan.FromMilliseconds(400 * attempt);
                _logger.Error($"[Streaming] {operationName} transient error. Retry {attempt}/{attempts - 1} sau {delay.TotalMilliseconds:0}ms.", ex);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        using var finalTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        finalTimeoutCts.CancelAfter(timeout);
        return await operation(finalTimeoutCts.Token).WaitAsync(timeout, finalTimeoutCts.Token).ConfigureAwait(false);
    }

    private async Task<T> RunAdvancedWithResilienceAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan configuredTimeout,
        string operationName,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(configuredTimeout.TotalSeconds, 5, 12));
        var attempts = Math.Clamp(Settings.SpeechRecognition.ApiRetryCount, 0, 2) + 1;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await WaitForAdvancedCircuitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                var value = await operation(timeoutCts.Token).WaitAsync(timeout, timeoutCts.Token).ConfigureAwait(false);
                ResetAdvancedCircuit();
                return value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientGoogleError(ex) || ex is OperationCanceledException)
            {
                lastError = ex;
                RegisterAdvancedTransientFailure(operationName, ex);
                if (attempt >= attempts)
                {
                    break;
                }

                var baseDelayMs = ex is RpcException { StatusCode: StatusCode.ResourceExhausted } ? 2000 : 500;
                var delayMs = Math.Min(5000, baseDelayMs * (1 << (attempt - 1))) + Random.Shared.Next(100, 350);
                _logger.Error($"[Advanced Hybrid] {operationName} transient failure. Retry {attempt}/{attempts - 1} in {delayMs}ms.", ex);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? new InvalidOperationException($"{operationName} failed without an error.");
    }

    private Task<T> RunQueuedGoogleOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        string operationName,
        CancellationToken cancellationToken)
        => Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
            ? RunAdvancedWithResilienceAsync(operation, timeout, operationName, cancellationToken)
            : RunWithRetryAsync(
                operation,
                timeout,
                Settings.SpeechRecognition.ApiRetryCount,
                operationName,
                cancellationToken);

    private async Task WaitForAdvancedCircuitAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_advancedCircuitSyncRoot)
        {
            wait = _advancedCircuitOpenUntilUtc - DateTime.UtcNow;
        }

        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        UpdateRuntimeState(state => state.IsReconnectingStt = true);
        StatusChanged?.Invoke(this, $"Google đang quá tải. Hybrid nâng cao tự thử lại sau {Math.Ceiling(wait.TotalSeconds):0} giây.");
        try
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            UpdateRuntimeState(state => state.IsReconnectingStt = false);
        }
    }

    private void RegisterAdvancedTransientFailure(string operationName, Exception exception)
    {
        lock (_advancedCircuitSyncRoot)
        {
            _advancedConsecutiveTransientFailures++;
            if (_advancedConsecutiveTransientFailures < 3)
            {
                return;
            }

            _advancedCircuitOpenUntilUtc = DateTime.UtcNow.AddSeconds(10);
            _logger.Error($"[Advanced Hybrid] Circuit opened for 10 seconds after {_advancedConsecutiveTransientFailures} transient failures in {operationName}.", exception);
        }
    }

    private void ResetAdvancedCircuit()
    {
        lock (_advancedCircuitSyncRoot)
        {
            _advancedConsecutiveTransientFailures = 0;
            _advancedCircuitOpenUntilUtc = DateTime.MinValue;
        }
    }

    private static bool IsTransientGoogleError(Exception exception)
    {
        if (exception is TimeoutException)
        {
            return true;
        }

        if (exception is RpcException rpc)
        {
            return rpc.StatusCode is StatusCode.Unavailable
                or StatusCode.DeadlineExceeded
                or StatusCode.ResourceExhausted
                or StatusCode.Aborted
                or StatusCode.Internal
                or StatusCode.Unknown;
        }

        var message = exception.ToString();
        return message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("temporar", StringComparison.OrdinalIgnoreCase)
            || message.Contains("503", StringComparison.OrdinalIgnoreCase)
            || message.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        if (_utteranceChannel is null)
        {
            return;
        }

        try
        {
            await foreach (var utterance in _utteranceChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                DecrementPendingUtterances();
                await ProcessUtteranceAsync(utterance, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessHybridRecognitionQueueAsync(CancellationToken cancellationToken)
    {
        if (_utteranceChannel is null || _translationQueue is null)
        {
            return;
        }

        var translationQueue = _translationQueue;
        try
        {
            await foreach (var utterance in _utteranceChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                DecrementPendingUtterances();
                await ProcessHybridUtteranceRecognitionAsync(utterance, translationQueue, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAdvancedRecognitionWorkersAsync(CancellationToken cancellationToken)
    {
        var workers = Enumerable.Range(1, AdvancedSttWorkerCount)
            .Select(workerId => ProcessAdvancedRecognitionWorkerAsync(workerId, cancellationToken))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task ProcessAdvancedRecognitionWorkerAsync(int workerId, CancellationToken cancellationToken)
    {
        if (_utteranceChannel is null || _advancedRecognitionQueue is null)
        {
            return;
        }

        var resultWriter = _advancedRecognitionQueue.Writer;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var utterance in _utteranceChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    DecrementPendingUtterances();
                    try
                    {
                        var outcome = await RecognizeAdvancedUtteranceAsync(utterance, workerId, cancellationToken).ConfigureAwait(false);
                        await resultWriter.WriteAsync(outcome, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _advancedOutstandingRecognitionCount);
                    }
                }

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error($"[Advanced Hybrid] STT worker {workerId} failed and will restart in one second.", ex);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<AdvancedRecognitionOutcome> RecognizeAdvancedUtteranceAsync(
        AudioUtterance utterance,
        int workerId,
        CancellationToken cancellationToken)
    {
        var queueWait = DateTime.Now - utterance.CapturedAt;
        if (queueWait > AdvancedMaximumUtteranceAge)
        {
            return AdvancedRecognitionOutcome.Failed(utterance, "Câu đã quá cũ nên được bỏ qua", queueWait.TotalMilliseconds);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.Recognizing);
            PublishQueueStatus(utterance.SequenceNumber);
            SetState(InterpreterState.ProcessingSpeech, $"Đang nhận dạng: Câu #{utterance.SequenceNumber}");
            _logger.Info($"[Advanced Hybrid #{utterance.SequenceNumber}] STT worker {workerId} started. QueueWait={queueWait.TotalMilliseconds:0}ms; Duration={utterance.Duration.TotalSeconds:0.00}s");

            var recognition = await RunAdvancedWithResilienceAsync(
                token => _googlePipeline.RecognizeSpeechAsync(utterance.AudioData, token),
                TimeSpan.FromSeconds(Settings.SpeechRecognition.RecognitionTimeoutSeconds),
                $"STT #{utterance.SequenceNumber}",
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (recognition is null || string.IsNullOrWhiteSpace(recognition.Text))
            {
                return AdvancedRecognitionOutcome.Failed(
                    utterance,
                    "Không nhận dạng được nội dung",
                    queueWait.TotalMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds);
            }

            var sourceLanguage = recognition.Language == SupportedLanguage.Unknown
                ? InferLanguageFromText(recognition.Text)
                : recognition.Language;
            var targetLanguage = LanguageHelper.GetTargetLanguage(sourceLanguage);
            if (targetLanguage == SupportedLanguage.Unknown)
            {
                return AdvancedRecognitionOutcome.Failed(
                    utterance,
                    "Không xác định được ngôn ngữ",
                    queueWait.TotalMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds,
                    recognition.Text,
                    recognition.Confidence);
            }

            var transcript = new TranscriptUtterance
            {
                SequenceNumber = utterance.SequenceNumber,
                CreatedAt = utterance.CapturedAt,
                Text = recognition.Text,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                Confidence = recognition.Confidence,
                RecognitionMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                QueueWaitMilliseconds = queueWait.TotalMilliseconds
            };
            SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.SttFinal);
            _logger.Info($"[Advanced Hybrid #{utterance.SequenceNumber}] STT completed. Worker={workerId}; Language={sourceLanguage}; Recognition={stopwatch.Elapsed.TotalMilliseconds:0}ms; Text={recognition.Text}");
            return AdvancedRecognitionOutcome.Completed(utterance, transcript);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error($"[Advanced Hybrid #{utterance.SequenceNumber}] STT failed; the next utterance will continue.", ex);
            return AdvancedRecognitionOutcome.Failed(
                utterance,
                ToUserMessage(ex),
                queueWait.TotalMilliseconds,
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task ProcessAdvancedRecognitionResultsAsync(CancellationToken cancellationToken)
    {
        if (_advancedRecognitionQueue is null || _translationQueue is null)
        {
            return;
        }

        var buffered = new SortedDictionary<long, AdvancedRecognitionOutcome>();
        var expectedSequence = 1L;
        AdvancedTranscriptAggregate? aggregate = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                AdvancedRecognitionOutcome? ordered;
                if (aggregate is null)
                {
                    try
                    {
                        ordered = await ReadNextAdvancedOutcomeAsync(
                            _advancedRecognitionQueue.Reader,
                            buffered,
                            expectedSequence,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                        return;
                    }

                    expectedSequence++;
                }
                else
                {
                    var wait = GetAdvancedContinuationWindow(aggregate.Text);
                    using var continuationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    continuationCts.CancelAfter(wait);
                    try
                    {
                        ordered = await ReadNextAdvancedOutcomeAsync(
                            _advancedRecognitionQueue.Reader,
                            buffered,
                            expectedSequence,
                            continuationCts.Token).ConfigureAwait(false);
                        expectedSequence++;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (ShouldKeepAdvancedAggregateOpen(aggregate))
                        {
                            continue;
                        }

                        await EnqueueAdvancedAggregateAsync(aggregate, _translationQueue, cancellationToken).ConfigureAwait(false);
                        aggregate = null;
                        continue;
                    }
                    catch (ChannelClosedException)
                    {
                        await EnqueueAdvancedAggregateAsync(aggregate, _translationQueue, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                if (ordered.Transcript is null)
                {
                    if (aggregate is not null)
                    {
                        await EnqueueAdvancedAggregateAsync(aggregate, _translationQueue, cancellationToken).ConfigureAwait(false);
                        aggregate = null;
                    }

                    PublishAdvancedRecognitionFailure(ordered);
                    continue;
                }

                Interlocked.Increment(ref _metrics.TotalSttFinal);
                if (aggregate is null)
                {
                    aggregate = AdvancedTranscriptAggregate.Start(ordered);
                    SetAdvancedAggregatePreview(aggregate.Text, isActive: true, aggregate.SourceLanguage);
                    StatusChanged?.Invoke(this, "Đã nhận nội dung. Đang chờ người nói hoàn tất câu...");
                    continue;
                }

                if (CanMergeAdvancedTranscript(aggregate, ordered))
                {
                    aggregate.Append(ordered);
                    SetAdvancedAggregatePreview(aggregate.Text, isActive: true, aggregate.SourceLanguage);
                    SetUtteranceStage(ordered.Utterance.SequenceNumber, UtteranceStage.Merged);
                    _logger.Info($"[Advanced Hybrid #{ordered.Utterance.SequenceNumber}] Merged into sentence #{aggregate.SequenceNumber}: {aggregate.Text}");
                    continue;
                }

                await EnqueueAdvancedAggregateAsync(aggregate, _translationQueue, cancellationToken).ConfigureAwait(false);
                aggregate = AdvancedTranscriptAggregate.Start(ordered);
                SetAdvancedAggregatePreview(aggregate.Text, isActive: true, aggregate.SourceLanguage);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("[Advanced Hybrid] Sentence aggregation worker failed.", ex);
            throw;
        }
    }

    private static async Task<AdvancedRecognitionOutcome> ReadNextAdvancedOutcomeAsync(
        ChannelReader<AdvancedRecognitionOutcome> reader,
        SortedDictionary<long, AdvancedRecognitionOutcome> buffered,
        long expectedSequence,
        CancellationToken cancellationToken)
    {
        if (buffered.Remove(expectedSequence, out var ready))
        {
            return ready;
        }

        while (true)
        {
            var outcome = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (outcome.Utterance.SequenceNumber == expectedSequence)
            {
                return outcome;
            }

            buffered[outcome.Utterance.SequenceNumber] = outcome;
        }
    }

    private bool ShouldKeepAdvancedAggregateOpen(AdvancedTranscriptAggregate aggregate)
    {
        if (DateTime.UtcNow - aggregate.StartedAtUtc >= AdvancedMaximumAggregationDuration)
        {
            return false;
        }

        return _advancedSpeechActive || Volatile.Read(ref _advancedOutstandingRecognitionCount) > 0;
    }

    private static bool CanMergeAdvancedTranscript(
        AdvancedTranscriptAggregate aggregate,
        AdvancedRecognitionOutcome next)
        => next.Transcript is not null
            && aggregate.SourceLanguage == next.Transcript.SourceLanguage
            && aggregate.Text.Length + next.Transcript.Text.Length <= 1200
            && DateTime.UtcNow - aggregate.StartedAtUtc < AdvancedMaximumAggregationDuration;

    private async Task EnqueueAdvancedAggregateAsync(
        AdvancedTranscriptAggregate aggregate,
        Channel<TranscriptUtterance> translationQueue,
        CancellationToken cancellationToken)
    {
        var transcript = ReconcileAdvancedTranscriptForTranslation(aggregate.ToTranscript());
        SetAdvancedAggregatePreview(transcript.Text, isActive: false, transcript.SourceLanguage);
        if (DateTime.Now - transcript.CreatedAt > AdvancedMaximumUtteranceAge)
        {
            SetUtteranceStage(transcript.SequenceNumber, UtteranceStage.Failed, "Câu đã quá cũ trước khi dịch");
            Interlocked.Increment(ref _metrics.TotalFailed);
            PublishFailedStreamingResult(transcript, "Câu đã quá cũ nên được bỏ qua");
            PublishQueueStatus(null);
            return;
        }

        await translationQueue.Writer.WriteAsync(transcript, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _pendingUtteranceCount);
        Interlocked.Increment(ref _metrics.TotalTranslationQueued);
        SetUtteranceStage(transcript.SequenceNumber, UtteranceStage.QueuedForTranslation);
        _logger.Info($"[Advanced Hybrid #{transcript.SequenceNumber}] Aggregation completed with {aggregate.FragmentCount} fragment(s): {transcript.Text}");
        PublishQueueStatus(null);
    }

    private TranscriptUtterance ReconcileAdvancedTranscriptForTranslation(TranscriptUtterance batchTranscript)
    {
        string liveTranscript;
        SupportedLanguage liveLanguage;
        lock (_advancedPreviewSyncRoot)
        {
            if (_advancedLiveCandidates.TryGetValue(batchTranscript.SourceLanguage, out var matchingCandidate))
            {
                liveTranscript = matchingCandidate.Text;
                liveLanguage = matchingCandidate.Language;
            }
            else
            {
                liveTranscript = ComposeAdvancedLiveTranscriptLocked();
                liveLanguage = _advancedLivePreviewLanguage;
            }
        }

        if (string.IsNullOrWhiteSpace(liveTranscript)
            || liveLanguage != batchTranscript.SourceLanguage
            || CountAdvancedTranscriptWords(liveTranscript) <= CountAdvancedTranscriptWords(batchTranscript.Text)
            || !AreAdvancedTranscriptsRelated(batchTranscript.Text, liveTranscript))
        {
            return batchTranscript;
        }

        _logger.Info(
            $"[Advanced Hybrid #{batchTranscript.SequenceNumber}] Translation input upgraded from batch STT to the fuller streaming transcript. " +
            $"Batch={batchTranscript.Text}; Streaming={liveTranscript}");
        return new TranscriptUtterance
        {
            SequenceNumber = batchTranscript.SequenceNumber,
            CreatedAt = batchTranscript.CreatedAt,
            Text = liveTranscript,
            SourceLanguage = batchTranscript.SourceLanguage,
            TargetLanguage = batchTranscript.TargetLanguage,
            Confidence = batchTranscript.Confidence,
            RecognitionMilliseconds = batchTranscript.RecognitionMilliseconds,
            QueueWaitMilliseconds = batchTranscript.QueueWaitMilliseconds
        };
    }

    private void PublishAdvancedRecognitionFailure(AdvancedRecognitionOutcome outcome)
    {
        var sequence = outcome.Utterance.SequenceNumber;
        SetUtteranceStage(sequence, UtteranceStage.Failed, outcome.ErrorMessage);
        Interlocked.Increment(ref _metrics.TotalFailed);
        TranslationCompleted?.Invoke(this, new TranslationResult
        {
            Timestamp = outcome.Utterance.CapturedAt,
            Engine = InterpreterEngineType.GoogleCloudAdvancedHybridPipeline,
            OriginalText = outcome.RecognizedText,
            Confidence = outcome.Confidence,
            RecognitionMilliseconds = outcome.RecognitionMilliseconds,
            TotalMilliseconds = (DateTime.Now - outcome.Utterance.CapturedAt).TotalMilliseconds,
            Success = false,
            ErrorMessage = outcome.ErrorMessage
        });
        StatusChanged?.Invoke(this, $"Câu #{sequence}: {outcome.ErrorMessage}. Hệ thống tiếp tục câu kế tiếp.");
        PublishQueueStatus(null);
    }

    private static TimeSpan GetAdvancedContinuationWindow(string text)
    {
        var normalized = text.Trim();
        if (normalized.EndsWith('?') || normalized.EndsWith('!'))
        {
            return TimeSpan.FromMilliseconds(800);
        }

        return LooksLikeIncompleteSentence(normalized)
            ? TimeSpan.FromMilliseconds(2500)
            : TimeSpan.FromMilliseconds(1500);
    }

    private static bool LooksLikeIncompleteSentence(string text)
    {
        var normalized = text.Trim().TrimEnd('.', ',', ';', ':', '…').Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        string[] continuationEndings =
        [
            "và", "nhưng", "vì", "nếu", "thì", "để", "khi", "mà", "là", "của", "với", "cho",
            "từ", "đến", "về", "trong", "theo", "hoặc", "do", "bởi vì", "tuy nhiên",
            "그리고", "하지만", "때문에", "만약", "그러면", "해서", "하고", "는데", "지만", "거나", "려고", "위해"
        ];

        return continuationEndings.Any(ending =>
            normalized.Equals(ending, StringComparison.Ordinal)
            || normalized.EndsWith($" {ending}", StringComparison.Ordinal)
            || normalized.EndsWith(ending, StringComparison.Ordinal) && ContainsHangul(normalized));
    }

    private async Task ProcessHybridUtteranceRecognitionAsync(
        AudioUtterance utterance,
        Channel<TranscriptUtterance> translationQueue,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.Now;
        try
        {
            PublishQueueStatus(utterance.SequenceNumber);
            SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.SttFinal);
            SetState(InterpreterState.ProcessingSpeech, "Dang nhan dang ngon ngu va noi dung cau noi.");
            _logger.Info($"[Hybrid #{utterance.SequenceNumber}] Recognition Started. Duration={utterance.Duration.TotalSeconds:0.00}s");

            var recognition = await RunWithRetryAsync(
                token => _googlePipeline.RecognizeSpeechAsync(utterance.AudioData, token),
                TimeSpan.FromSeconds(Math.Clamp(Settings.SpeechRecognition.RecognitionTimeoutSeconds, 10, 30)),
                Settings.SpeechRecognition.ApiRetryCount,
                $"Hybrid STT #{utterance.SequenceNumber}",
                cancellationToken).ConfigureAwait(false);

            if (recognition is null || string.IsNullOrWhiteSpace(recognition.Text))
            {
                TranslationCompleted?.Invoke(this, new TranslationResult
                {
                    Timestamp = startedAt,
                    Engine = InterpreterEngineType.GoogleCloudHybridPipeline,
                    Success = false,
                    ErrorMessage = "Da bo qua"
                });
                PublishQueueStatus(null);
                return;
            }

            var sourceLanguage = recognition.Language == SupportedLanguage.Unknown
                ? InferLanguageFromText(recognition.Text)
                : recognition.Language;
            var targetLanguage = LanguageHelper.GetTargetLanguage(sourceLanguage);
            if (targetLanguage == SupportedLanguage.Unknown)
            {
                TranslationCompleted?.Invoke(this, new TranslationResult
                {
                    Timestamp = startedAt,
                    Engine = InterpreterEngineType.GoogleCloudHybridPipeline,
                    SourceLanguage = sourceLanguage,
                    OriginalText = recognition.Text,
                    Confidence = recognition.Confidence,
                    Success = false,
                    ErrorMessage = "Khong xac dinh ngon ngu"
                });
                PublishQueueStatus(null);
                return;
            }

            var transcript = new TranscriptUtterance
            {
                SequenceNumber = utterance.SequenceNumber,
                CreatedAt = startedAt,
                Text = recognition.Text,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                Confidence = recognition.Confidence
            };

            _logger.Info($"[Hybrid #{utterance.SequenceNumber}] STT Final. RawLanguageCode={recognition.RawLanguageCode}; Source={sourceLanguage}; Target={targetLanguage}; Text={recognition.Text}");
            ContentPreviewChanged?.Invoke(
                this,
                new InterpreterContentPreviewEventArgs("Nội dung nói", recognition.Text, false, false, sourceLanguage));

            if (!translationQueue.Writer.TryWrite(transcript))
            {
                PublishFailedStreamingResult(transcript, "Hang doi dich da day");
                PublishQueueStatus(null);
                return;
            }

            Interlocked.Increment(ref _pendingUtteranceCount);
            Interlocked.Increment(ref _metrics.TotalSttFinal);
            Interlocked.Increment(ref _metrics.TotalTranslationQueued);
            SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.QueuedForTranslation);
            PublishQueueStatus(null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error($"[Hybrid #{utterance.SequenceNumber}] Recognition failed.", ex);
            TranslationCompleted?.Invoke(this, new TranslationResult
            {
                Timestamp = startedAt,
                Engine = InterpreterEngineType.GoogleCloudHybridPipeline,
                Success = false,
                ErrorMessage = "Co loi"
            });
            StatusChanged?.Invoke(this, $"Cau #{utterance.SequenceNumber} loi khi nhan dang. He thong tiep tuc cau ke tiep.");
        }
    }

    private async Task ProcessTranslationQueueAsync(CancellationToken cancellationToken)
    {
        if (_translationQueue is null)
        {
            return;
        }

        try
        {
            await foreach (var utterance in _translationQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    DecrementPendingUtterances();
                    await ProcessTranscriptUtteranceAsync(utterance, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"[Streaming #{utterance.SequenceNumber}] Translation worker item failed.", ex);
                    PublishFailedStreamingResult(utterance, "Có lỗi");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessTranscriptUtteranceAsync(TranscriptUtterance utterance, CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var result = new TranslationResult
        {
            Timestamp = utterance.CreatedAt,
            Engine = Settings.EngineType,
            SourceLanguage = utterance.SourceLanguage,
            TargetLanguage = utterance.TargetLanguage,
            OriginalText = utterance.Text,
            Confidence = utterance.Confidence,
            RecognitionMilliseconds = utterance.RecognitionMilliseconds,
            AiProcessingMilliseconds = utterance.RecognitionMilliseconds
        };

        try
        {
            PublishQueueStatus(utterance.SequenceNumber);
            if (Settings.EngineType == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                && DateTime.Now - utterance.CreatedAt > AdvancedMaximumUtteranceAge)
            {
                SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.Failed, "Câu đã quá cũ trước khi dịch");
                Interlocked.Increment(ref _metrics.TotalFailed);
                PublishFailedStreamingResult(utterance, "Câu đã quá cũ nên được bỏ qua");
                PublishQueueStatus(null);
                return;
            }

            if (IsDuplicate(result.OriginalText, result.SourceLanguage))
            {
                _logger.Info($"[Streaming #{utterance.SequenceNumber}] Bo qua transcript trung lap.");
                PublishQueueStatus(null);
                return;
            }

            if (Settings.RecognitionOnlyMode)
            {
                totalStopwatch.Stop();
                result.Success = true;
                result.TotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
                TranslationCompleted?.Invoke(this, result);
                _logger.Info($"[Streaming #{utterance.SequenceNumber}] Translation Completed (recognition only).");
                PublishQueueStatus(null);
                return;
            }

            UpdateRuntimeState(state => state.IsTranslating = true);
            SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.Translating);
            _logger.Info($"[Streaming #{utterance.SequenceNumber}] Translation Started.");
            SetState(InterpreterState.Translating, $"Đang dịch: Câu #{utterance.SequenceNumber}");
            var stage = Stopwatch.StartNew();
            result.TranslatedText = await RunQueuedGoogleOperationAsync(
                token => _googlePipeline.TranslateTextAsync(
                    result.OriginalText,
                    result.SourceLanguage,
                    result.TargetLanguage,
                    token),
                TimeSpan.FromSeconds(Math.Clamp(Settings.SpeechRecognition.TranslationTimeoutSeconds, 10, 30)),
                $"Translation #{utterance.SequenceNumber}",
                cancellationToken).ConfigureAwait(false);
            stage.Stop();
            result.TranslationMilliseconds = stage.Elapsed.TotalMilliseconds;
            result.AiProcessingMilliseconds = result.RecognitionMilliseconds + result.TranslationMilliseconds;
            Interlocked.Increment(ref _metrics.TotalTranslated);
            SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.Translated);
            _logger.Info($"[Streaming #{utterance.SequenceNumber}] Translation Completed.");
            ContentPreviewChanged?.Invoke(
                this,
                new InterpreterContentPreviewEventArgs("Nội dung dịch", result.TranslatedText, true, false, result.TargetLanguage));

            if ((Settings.EngineType is InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
                && !IsSafeForPlayback(result))
            {
                throw new InvalidOperationException("Kết quả dịch không hợp lệ nên không phát TTS.");
            }

            totalStopwatch.Stop();
            result.TotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;

            await EnqueueTtsItemAsync(
                new TtsItem
                {
                    SequenceNumber = utterance.SequenceNumber,
                    TargetLanguage = result.TargetLanguage,
                    OriginalText = result.OriginalText,
                    TranslatedText = result.TranslatedText,
                    Result = result
                },
                cancellationToken).ConfigureAwait(false);

            PublishQueueStatus(null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            result.TotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
            result.Success = false;
            result.ErrorMessage = "Có lỗi";
            SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.Failed, ex.Message);
            Interlocked.Increment(ref _metrics.TotalFailed);
            TranslationCompleted?.Invoke(this, result);
            _logger.Error($"[Streaming #{utterance.SequenceNumber}] Translation failed.", ex);
            StatusChanged?.Invoke(this, $"Câu #{utterance.SequenceNumber} lỗi khi dịch. Hệ thống tiếp tục câu kế tiếp.");
        }
        finally
        {
            UpdateRuntimeState(state => state.IsTranslating = false);
        }
    }

    private void PublishFailedStreamingResult(TranscriptUtterance utterance, string errorMessage)
    {
        TranslationCompleted?.Invoke(this, new TranslationResult
        {
            Timestamp = utterance.CreatedAt,
            Engine = Settings.EngineType,
            SourceLanguage = utterance.SourceLanguage,
            TargetLanguage = utterance.TargetLanguage,
            OriginalText = utterance.Text,
            Confidence = utterance.Confidence,
            Success = false,
            ErrorMessage = errorMessage
        });
    }

    private async Task EnqueueTtsItemAsync(TtsItem item, CancellationToken cancellationToken)
    {
        if (_ttsQueue is null)
        {
            return;
        }

        Interlocked.Increment(ref _pendingTtsCount);
        SetUtteranceStage(item.SequenceNumber, UtteranceStage.QueuedForTts);
        _logger.Info($"[Streaming #{item.SequenceNumber}] QueuedForTTS. PendingTTS={Volatile.Read(ref _pendingTtsCount)}");
        await _ttsQueue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        PublishQueueStatus(null);
    }

    private async Task ProcessTtsQueueAsync(CancellationToken cancellationToken)
    {
        if (_ttsQueue is null)
        {
            return;
        }

        try
        {
            await foreach (var item in _ttsQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    DecrementPendingTtsItems();
                    await ProcessTtsItemAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"[Streaming #{item.SequenceNumber}] TTS worker item failed.", ex);
                    item.Result.Success = false;
                    item.Result.ErrorMessage = "Có lỗi";
                    TranslationCompleted?.Invoke(this, item.Result);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessTtsItemAsync(TtsItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (item.Result.Engine == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                && DateTime.Now - item.Result.Timestamp > AdvancedMaximumUtteranceAge)
            {
                item.Result.Success = false;
                item.Result.ErrorMessage = "Câu đã quá cũ nên không tạo giọng nói";
                item.Result.TotalMilliseconds = (DateTime.Now - item.Result.Timestamp).TotalMilliseconds;
                SetUtteranceStage(item.SequenceNumber, UtteranceStage.Failed, item.Result.ErrorMessage);
                Interlocked.Increment(ref _metrics.TotalFailed);
                TranslationCompleted?.Invoke(this, item.Result);
                return;
            }

            UpdateRuntimeState(state => state.IsSynthesizing = true);
            SetUtteranceStage(item.SequenceNumber, UtteranceStage.Synthesizing);
            SetState(InterpreterState.Synthesizing, $"Đang tạo giọng nói: Câu #{item.SequenceNumber}");
            _logger.Info($"[Streaming #{item.SequenceNumber}] TTS Started.");
            var stage = Stopwatch.StartNew();
            var ttsAudio = await RunQueuedGoogleOperationAsync(
                token => _engineFactory
                    .GetSpeechSynthesisService(item.Result.Engine)
                    .SynthesizeAsync(item.TranslatedText, item.TargetLanguage, token),
                TimeSpan.FromSeconds(Math.Clamp(Settings.SpeechRecognition.TtsTimeoutSeconds, 10, 30)),
                $"TTS #{item.SequenceNumber}",
                cancellationToken).ConfigureAwait(false);
            stage.Stop();
            item.Result.SynthesisMilliseconds = stage.Elapsed.TotalMilliseconds;
            item.Result.TotalMilliseconds += stage.Elapsed.TotalMilliseconds;
            Interlocked.Increment(ref _metrics.TotalTtsCompleted);
            SetUtteranceStage(item.SequenceNumber, UtteranceStage.ReadyForPlayback);
            _logger.Info($"[Streaming #{item.SequenceNumber}] TTS Completed.");

            await EnqueuePlaybackItemAsync(
                new PlaybackItem
                {
                    SequenceNumber = item.SequenceNumber,
                    TargetLanguage = item.TargetLanguage,
                    AudioData = ttsAudio,
                    OriginalText = item.OriginalText,
                    TranslatedText = item.TranslatedText,
                    Result = item.Result
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            item.Result.Success = false;
            item.Result.ErrorMessage = "Có lỗi";
            SetUtteranceStage(item.SequenceNumber, UtteranceStage.Failed, ex.Message);
            Interlocked.Increment(ref _metrics.TotalFailed);
            TranslationCompleted?.Invoke(this, item.Result);
            _logger.Error($"[Streaming #{item.SequenceNumber}] TTS failed.", ex);
            StatusChanged?.Invoke(this, $"Câu #{item.SequenceNumber} lỗi khi tạo giọng nói. Hệ thống tiếp tục câu kế tiếp.");
        }
        finally
        {
            UpdateRuntimeState(state => state.IsSynthesizing = false);
        }
    }

    private async Task EnqueuePlaybackItemAsync(PlaybackItem item, CancellationToken cancellationToken)
    {
        if (_playbackQueue is null)
        {
            return;
        }

        Interlocked.Increment(ref _pendingPlaybackCount);
        SetUtteranceStage(item.SequenceNumber, UtteranceStage.ReadyForPlayback);
        _logger.Info($"[Streaming #{item.SequenceNumber}] QueuedForPlayback. PendingPlayback={Volatile.Read(ref _pendingPlaybackCount)}");
        await _playbackQueue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        PublishQueueStatus(null);
    }

    private async Task ProcessPlaybackQueueAsync(CancellationToken cancellationToken)
    {
        if (_playbackQueue is null)
        {
            return;
        }

        try
        {
            await foreach (var item in _playbackQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    DecrementPendingPlaybackItems();
                    await ProcessPlaybackItemAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"[Streaming #{item.SequenceNumber}] Playback worker item failed.", ex);
                    item.Result.Success = false;
                    item.Result.ErrorMessage = "Có lỗi";
                    TranslationCompleted?.Invoke(this, item.Result);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessPlaybackItemAsync(PlaybackItem item, CancellationToken cancellationToken)
    {
        var playbackWatch = Stopwatch.StartNew();
        try
        {
            if (item.Result.Engine == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                && DateTime.Now - item.Result.Timestamp > AdvancedMaximumUtteranceAge)
            {
                item.Result.Success = false;
                item.Result.ErrorMessage = "Câu đã quá cũ nên không phát âm thanh";
                item.Result.TotalMilliseconds = (DateTime.Now - item.Result.Timestamp).TotalMilliseconds;
                SetUtteranceStage(item.SequenceNumber, UtteranceStage.Failed, item.Result.ErrorMessage);
                Interlocked.Increment(ref _metrics.TotalFailed);
                TranslationCompleted?.Invoke(this, item.Result);
                return;
            }

            UpdateRuntimeState(state => state.IsPlaying = true);
            SetUtteranceStage(item.SequenceNumber, UtteranceStage.Playing);
            SetState(InterpreterState.Playing, $"Đang phát: Câu #{item.SequenceNumber}");
            _logger.Info($"[Streaming #{item.SequenceNumber}] Playback Started.");
            await PlayTranslatedAudioAsync(item.AudioData, item.TargetLanguage, cancellationToken).ConfigureAwait(false);
            playbackWatch.Stop();

            item.Result.PlaybackPreparationMilliseconds = playbackWatch.Elapsed.TotalMilliseconds;
            item.Result.TotalMilliseconds += playbackWatch.Elapsed.TotalMilliseconds;
            item.Result.Success = true;
            if (item.Result.Engine is InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
            {
                item.Result.TotalMilliseconds = (DateTime.Now - item.Result.Timestamp).TotalMilliseconds;
            }
            TranslationCompleted?.Invoke(this, item.Result);
            Interlocked.Increment(ref _metrics.TotalPlaybackCompleted);
            SetUtteranceStage(item.SequenceNumber, UtteranceStage.Completed);
            if (item.Result.Engine == InterpreterEngineType.GoogleCloudAdvancedHybridPipeline)
            {
                _logger.Info(
                    $"[Advanced Hybrid #{item.SequenceNumber}] Completed. " +
                    $"STT={item.Result.RecognitionMilliseconds:0}ms; Translate={item.Result.TranslationMilliseconds:0}ms; " +
                    $"TTS={item.Result.SynthesisMilliseconds:0}ms; Playback={item.Result.PlaybackPreparationMilliseconds:0}ms; " +
                    $"EndToEnd={item.Result.TotalMilliseconds:0}ms");
            }
            else if (item.Result.Engine == InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline)
            {
                _logger.Info(
                    $"[Adaptive Hybrid #{item.SequenceNumber}] Completed. " +
                    $"STT={item.Result.RecognitionMilliseconds:0}ms; Translate={item.Result.TranslationMilliseconds:0}ms; " +
                    $"TTS={item.Result.SynthesisMilliseconds:0}ms; Playback={item.Result.PlaybackPreparationMilliseconds:0}ms; " +
                    $"EndToEnd={item.Result.TotalMilliseconds:0}ms");
            }
            else if (item.Result.Engine == InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
            {
                _logger.Info(
                    $"[Physical Mute Hybrid #{item.SequenceNumber}] Completed. " +
                    $"STT={item.Result.RecognitionMilliseconds:0}ms; Translate={item.Result.TranslationMilliseconds:0}ms; " +
                    $"TTS={item.Result.SynthesisMilliseconds:0}ms; Playback={item.Result.PlaybackPreparationMilliseconds:0}ms; " +
                    $"EndToEnd={item.Result.TotalMilliseconds:0}ms");
            }
            else
            {
                _logger.Info($"[Streaming #{item.SequenceNumber}] Playback Completed.");
            }
            PublishQueueStatus(null);

            if (_isRunning && State != InterpreterState.Error)
            {
                var message = Settings.EngineType switch
                {
                    InterpreterEngineType.GoogleCloudAdvancedHybridPipeline => "Đang nghe liên tục bằng Hybrid nâng cao.",
                    InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline => "Đang nghe bằng Hybrid thích ứng.",
                    InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline => "Đang chờ tín hiệu micro; tắt micro vật lý để chốt và dịch.",
                    _ => "Đang nghe liên tục bằng Google Streaming Speech-to-Text."
                };
                SetState(InterpreterState.Listening, message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            playbackWatch.Stop();
            item.Result.PlaybackPreparationMilliseconds = playbackWatch.Elapsed.TotalMilliseconds;
            item.Result.Success = false;
            item.Result.ErrorMessage = "Có lỗi";
            SetUtteranceStage(item.SequenceNumber, UtteranceStage.Failed, ex.Message);
            Interlocked.Increment(ref _metrics.TotalFailed);
            TranslationCompleted?.Invoke(this, item.Result);
            _logger.Error($"[Streaming #{item.SequenceNumber}] Phat audio that bai.", ex);
            StatusChanged?.Invoke(this, $"Câu #{item.SequenceNumber} lỗi khi phát âm thanh. Hệ thống tiếp tục câu kế tiếp.");
        }
        finally
        {
            UpdateRuntimeState(state => state.IsPlaying = false);
        }
    }

    private async Task ProcessUtteranceAsync(AudioUtterance utterance, CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var result = new TranslationResult
        {
            Timestamp = DateTime.Now,
            Engine = Settings.EngineType
        };

        try
        {
            var engine = _engineFactory.GetEngine(Settings.EngineType);
            SetState(InterpreterState.ProcessingSpeech, GetEngineProcessingStatus(engine.EngineType));

            _logger.Info($"[Utterance #{utterance.SequenceNumber}] Processing. Duration={utterance.Duration.TotalSeconds:0.00}s");
            PublishQueueStatus(utterance.SequenceNumber);

            var engineResult = await engine.ProcessAudioAsync(utterance.AudioData, cancellationToken).ConfigureAwait(false);
            if (engineResult is null || !engineResult.Success)
            {
                CopyEngineResult(engineResult, result);
                result.Success = false;
                result.ErrorMessage = engineResult?.ErrorMessage ?? "Đã bỏ qua";
                TranslationCompleted?.Invoke(this, result);
                SetState(InterpreterState.Listening, "Đang lắng nghe.");
                return;
            }

            CopyEngineResult(engineResult, result);

            if (!IsSafeForPlayback(result))
            {
                result.Success = false;
                result.ErrorMessage = "Kết quả không hợp lệ";
                TranslationCompleted?.Invoke(this, result);
                SetState(InterpreterState.Listening, "Đang lắng nghe.");
                return;
            }

            if (IsDuplicate(result.OriginalText, result.SourceLanguage))
            {
                _logger.Info("Bỏ qua nhận dạng trùng lặp.");
                SetState(InterpreterState.Listening, "Đang lắng nghe.");
                return;
            }

            ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Nội dung nói", result.OriginalText));
            ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Nội dung dịch", result.TranslatedText));
            _logger.Info($"{GetEngineDisplayName(result.Engine)}: {GetLanguageDisplayName(result.SourceLanguage)} -> {GetLanguageDisplayName(result.TargetLanguage)} | {result.OriginalText} | {result.TranslatedText}");

            if (Settings.RecognitionOnlyMode && result.Engine == InterpreterEngineType.GoogleCloudPipeline)
            {
                totalStopwatch.Stop();
                result.TotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
                result.Success = true;
                TranslationCompleted?.Invoke(this, result);
                SetState(InterpreterState.Listening, "Đang kiểm thử nhận dạng.");
                return;
            }

            SetState(InterpreterState.Synthesizing, $"Đang tạo giọng nói {GetLanguageDisplayName(result.TargetLanguage)}...");
            var stage = Stopwatch.StartNew();
            var ttsAudio = await _engineFactory
                .GetSpeechSynthesisService(result.Engine)
                .SynthesizeAsync(result.TranslatedText, result.TargetLanguage, cancellationToken)
                .ConfigureAwait(false);
            stage.Stop();
            result.SynthesisMilliseconds = stage.Elapsed.TotalMilliseconds;

            SetState(InterpreterState.Playing, GetPlaybackStatus(result.TargetLanguage));
            _logger.Info($"[Utterance #{utterance.SequenceNumber}] Playback.");
            stage.Restart();
            await PlayTranslatedAudioAsync(ttsAudio, result.TargetLanguage, cancellationToken).ConfigureAwait(false);
            stage.Stop();
            result.PlaybackPreparationMilliseconds = stage.Elapsed.TotalMilliseconds;

            totalStopwatch.Stop();
            result.TotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
            result.Success = true;
            TranslationCompleted?.Invoke(this, result);
            SetState(InterpreterState.Listening, "Đang lắng nghe.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            result.TotalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds;
            result.Success = false;
            result.ErrorMessage = "Có lỗi";
            TranslationCompleted?.Invoke(this, result);
            _logger.Error("Quy trình phiên dịch thất bại.", ex);
            SetState(InterpreterState.Error, ToUserMessage(ex));
        }
        finally
        {
            PublishQueueStatus(null);
            if (_isRunning && State != InterpreterState.Error)
            {
                SetState(InterpreterState.Listening, "Đang lắng nghe.");
            }
        }
    }

    private async Task PlayTranslatedAudioAsync(
        byte[] audioData,
        SupportedLanguage targetLanguage,
        CancellationToken cancellationToken)
    {
        var outputDevice = targetLanguage switch
        {
            SupportedLanguage.Vietnamese => _output1Device,
            SupportedLanguage.Korean => _output2Device,
            _ => null
        };

        if (outputDevice is null)
        {
            throw new InvalidOperationException("Chưa cấu hình thiết bị phát cho ngôn ngữ đích.");
        }

        var stopCaptureDuringRoomSpeakerPlayback = targetLanguage == SupportedLanguage.Vietnamese
            || Settings.EngineType is (InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline);
        var suppress = stopCaptureDuringRoomSpeakerPlayback || Settings.SuppressMicDuringHeadsetPlayback;
        if (suppress)
        {
            _suppressMicrophoneProcessing = true;
            _vad.Reset();
            if (Settings.EngineType is InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
                or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
                or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
            {
                _advancedSpeechActive = false;
                _advancedVad.Reset();
            }
        }

        try
        {
            if (stopCaptureDuringRoomSpeakerPlayback)
            {
                _logger.Info("Tạm dừng thu âm microphone trong khi loa phòng họp đang phát.");
                _audioService.StopCapture();
                MicrophoneLevelChanged?.Invoke(this, 0);
            }

            await _audioService.PlayAudioAsync(audioData, outputDevice, cancellationToken).ConfigureAwait(false);
            if (suppress)
            {
                await Task.Delay(Settings.PostPlaybackSilenceMs, cancellationToken).ConfigureAwait(false);
                _vad.Reset();
            }
        }
        finally
        {
            if (suppress)
            {
                _suppressMicrophoneProcessing = false;
            }

            if (stopCaptureDuringRoomSpeakerPlayback
                && _isRunning
                && _inputDevice is not null
                && !cancellationToken.IsCancellationRequested)
            {
                await ResumeCaptureAfterPlaybackAsync(cancellationToken).ConfigureAwait(false);
                _logger.Info("Đã mở lại microphone sau khi loa phòng họp phát xong.");
            }
        }
    }

    private async Task ResumeCaptureAfterPlaybackAsync(CancellationToken cancellationToken)
    {
        if (_inputDevice is null || !_isRunning || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                _vad.Reset();
                _audioService.StartCapture(_inputDevice);
                _logger.Info("Da mo lai microphone sau khi loa phong hop phat xong.");
                return;
            }
            catch (Exception ex) when (attempt == 1 && !cancellationToken.IsCancellationRequested)
            {
                _logger.Error("Mo lai microphone sau phat loa that bai. Thu lai sau 250ms.", ex);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error("Khong the mo lai microphone sau khi phat loa.", ex);
                UpdateRuntimeState(state => state.IsListening = false);
                SetState(InterpreterState.Error, "Khong mo lai duoc microphone sau khi phat TTS. Vui long kiem tra thiet bi am thanh.");
                return;
            }
        }
    }

    private (AudioDeviceInfo Input, AudioDeviceInfo Output1, AudioDeviceInfo Output2) ValidateDevices(
        AudioDeviceInfo inputDevice,
        AudioDeviceInfo output1Device,
        AudioDeviceInfo output2Device,
        string? credentialPath)
    {
        if (Settings.EngineType is InterpreterEngineType.GoogleCloudPipeline
            or InterpreterEngineType.GoogleCloudHybridPipeline
            or InterpreterEngineType.GoogleCloudStreamingPipeline
            or InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
            or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline
            && !_googlePipeline.IsInitialized
            && string.IsNullOrWhiteSpace(credentialPath)
            && string.IsNullOrWhiteSpace(_credentialPath))
        {
            throw new InvalidOperationException("Google Cloud chưa được cấu hình.");
        }

        if (Settings.EngineType == InterpreterEngineType.Gemini25Pro
            && string.IsNullOrWhiteSpace(Settings.Gemini.ApiKey))
        {
            throw new InvalidOperationException("Chưa cấu hình API Key Gemini.");
        }

        if (Settings.EngineType == InterpreterEngineType.GoogleCloudPipeline
            && Settings.SttEngineType == SttEngineType.DeepgramNova2
            && string.IsNullOrWhiteSpace(Settings.Deepgram.ApiKey))
        {
            throw new InvalidOperationException("Chưa cấu hình API Key Deepgram.");
        }

        var inputDevices = EnumerateInputDevices();
        var outputDevices = EnumerateOutputDevices();
        var currentInput = ResolveCurrentDevice(inputDevice, inputDevices);
        var currentOutput1 = ResolveCurrentDevice(output1Device, outputDevices);
        var currentOutput2 = ResolveCurrentDevice(output2Device, outputDevices);

        if (currentInput is null)
        {
            throw new InvalidOperationException("Không tìm thấy microphone phòng họp.");
        }

        if (currentOutput1 is null)
        {
            throw new InvalidOperationException("Không tìm thấy loa phòng họp.");
        }

        if (currentOutput2 is null)
        {
            throw new InvalidOperationException("Không tìm thấy tai nghe quản lý Hàn Quốc.");
        }

        if (currentInput.Id != inputDevice.Id
            || currentOutput1.Id != output1Device.Id
            || currentOutput2.Id != output2Device.Id)
        {
            _logger.Info(
                "Danh sách thiết bị âm thanh đã thay đổi; đã tự đồng bộ lại thiết bị hiện hành trước khi bắt đầu.");
        }

        return (currentInput, currentOutput1, currentOutput2);
    }

    private static AudioDeviceInfo? ResolveCurrentDevice(
        AudioDeviceInfo selectedDevice,
        IReadOnlyList<AudioDeviceInfo> currentDevices)
    {
        var exact = currentDevices.FirstOrDefault(device => device.Id == selectedDevice.Id);
        if (exact is not null)
        {
            return exact;
        }

        var sameNumberAndName = currentDevices.FirstOrDefault(device =>
            device.DeviceNumber == selectedDevice.DeviceNumber
            && string.Equals(device.Name, selectedDevice.Name, StringComparison.OrdinalIgnoreCase));
        if (sameNumberAndName is not null)
        {
            return sameNumberAndName;
        }

        return currentDevices
            .Where(device => string.Equals(device.Name, selectedDevice.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(device => Math.Abs(device.DeviceNumber - selectedDevice.DeviceNumber))
            .FirstOrDefault();
    }

    private async Task EnsureSelectedEngineInitializedAsync(
        InterpreterEngineType engineType,
        string? credentialPath,
        CancellationToken cancellationToken)
    {
        if (engineType is InterpreterEngineType.GoogleCloudPipeline
            or InterpreterEngineType.GoogleCloudHybridPipeline
            or InterpreterEngineType.GoogleCloudStreamingPipeline
            or InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
            or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline)
        {
            var path = string.IsNullOrWhiteSpace(credentialPath) ? _credentialPath : credentialPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("Google Cloud chưa được cấu hình.");
            }

            await _googlePipeline.ReinitializeIfNeededAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (engineType == InterpreterEngineType.Gemini25Pro)
        {
            if (string.IsNullOrWhiteSpace(Settings.Gemini.ApiKey))
            {
                throw new InvalidOperationException("Chưa cấu hình API Key Gemini.");
            }

            await _geminiLiveSession.TestAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new NotSupportedException("Mô hình phiên dịch không được hỗ trợ.");
    }

    private void PublishMicrophoneLevel(double rms)
    {
        var now = Environment.TickCount;
        if (now - _lastMicLevelUpdateTick < 75)
        {
            return;
        }

        _lastMicLevelUpdateTick = now;
        var level = Math.Clamp((int)Math.Round(rms * 1000), 0, 100);
        MicrophoneLevelChanged?.Invoke(this, level);
    }

    private bool IsDuplicate(string text, SupportedLanguage language)
    {
        var signature = $"{language}:{text.Trim().ToLowerInvariant()}";
        var now = DateTime.UtcNow;

        if (_lastSignature == signature && now - _lastSignatureAt < TimeSpan.FromSeconds(2))
        {
            _lastSignatureAt = now;
            return true;
        }

        _lastSignature = signature;
        _lastSignatureAt = now;
        return false;
    }

    private void SetState(InterpreterState state, string message)
    {
        lock (_stateSyncRoot)
        {
            State = state;
        }

        StateChanged?.Invoke(this, new InterpreterStateChangedEventArgs(state, message));
        StatusChanged?.Invoke(this, message);
    }

    private void PublishQueueStatus(long? processingSequence)
    {
        var pendingTranslations = Math.Max(0, Volatile.Read(ref _pendingUtteranceCount));
        var pendingTts = Math.Max(0, Volatile.Read(ref _pendingTtsCount));
        var pendingPlayback = Math.Max(0, Volatile.Read(ref _pendingPlaybackCount));
        UpdateRuntimeState(state =>
        {
            state.PendingTranslations = pendingTranslations;
            state.PendingPlaybackItems = pendingPlayback;
        });

        QueueStatusChanged?.Invoke(
            this,
            new UtteranceQueueStatusEventArgs(
                processingSequence,
                pendingTranslations + pendingTts,
                pendingPlayback));
    }

    private void UpdateRuntimeState(Action<PipelineRuntimeState> update)
    {
        lock (_runtimeState)
        {
            update(_runtimeState);
        }
    }

    private static string GetLanguageDisplayName(SupportedLanguage language)
        => language switch
        {
            SupportedLanguage.Vietnamese => "tiếng Việt",
            SupportedLanguage.Korean => "tiếng Hàn",
            _ => "không xác định"
        };

    private static SupportedLanguage InferLanguageFromText(string text)
    {
        if (ContainsHangul(text))
        {
            return SupportedLanguage.Korean;
        }

        return string.IsNullOrWhiteSpace(text) ? SupportedLanguage.Unknown : SupportedLanguage.Vietnamese;
    }

    private static bool IsQueuedGooglePipeline(InterpreterEngineType engineType)
        => engineType is InterpreterEngineType.GoogleCloudHybridPipeline
            or InterpreterEngineType.GoogleCloudStreamingPipeline
            or InterpreterEngineType.GoogleCloudAdvancedHybridPipeline
            or InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline
            or InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline;

    private static SupportedLanguage ResolveGoogleStreamingSourceLanguage(StreamingTranscriptEventArgs args)
    {
        if (ContainsHangul(args.Text))
        {
            return SupportedLanguage.Korean;
        }

        if (ContainsVietnameseDiacritic(args.Text))
        {
            return SupportedLanguage.Vietnamese;
        }

        if (args.StreamLanguage != SupportedLanguage.Unknown)
        {
            return args.StreamLanguage;
        }

        return args.Language == SupportedLanguage.Unknown
            ? InferLanguageFromText(args.Text)
            : args.Language;
    }

    private static bool ContainsHangul(string text)
    {
        foreach (var character in text)
        {
            if (character >= 0xAC00 && character <= 0xD7AF)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsVietnameseDiacritic(string text)
    {
        const string VietnameseDiacritics = "\u00e0\u00e1\u1ea1\u1ea3\u00e3\u00e2\u1ea7\u1ea5\u1ead\u1ea9\u1eab\u0103\u1eb1\u1eaf\u1eb7\u1eb3\u1eb5\u00e8\u00e9\u1eb9\u1ebb\u1ebd\u00ea\u1ec1\u1ebf\u1ec7\u1ec3\u1ec5\u00ec\u00ed\u1ecb\u1ec9\u0129\u00f2\u00f3\u1ecd\u1ecf\u00f5\u00f4\u1ed3\u1ed1\u1ed9\u1ed5\u1ed7\u01a1\u1edd\u1edb\u1ee3\u1edf\u1ee1\u00f9\u00fa\u1ee5\u1ee7\u0169\u01b0\u1eeb\u1ee9\u1ef1\u1eed\u1eef\u1ef3\u00fd\u1ef5\u1ef7\u1ef9\u0111";
        foreach (var character in text)
        {
            if (VietnameseDiacritics.Contains(char.ToLowerInvariant(character)))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetEngineDisplayName(InterpreterEngineType engineType)
        => engineType switch
        {
            InterpreterEngineType.Gemini25Pro => "1. Gemini Live 3.1 Flash",
            InterpreterEngineType.GoogleCloudPipeline => "2. Speech + Translate + TTS",
            InterpreterEngineType.GoogleCloudHybridPipeline => "3. Hybrid Speech + Async Queue",
            InterpreterEngineType.GoogleCloudStreamingPipeline => "4. Streaming Speech + Translate + TTS",
            InterpreterEngineType.GoogleCloudAdvancedHybridPipeline => "6. Hybrid nâng cao",
            InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline => "7. Hybrid thích ứng",
            InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline => "8. Hybrid chốt bằng micro",
            _ => "Không xác định"
        };

    private static string GetEngineProcessingStatus(InterpreterEngineType engineType)
        => engineType switch
        {
            InterpreterEngineType.Gemini25Pro => "Đang stream âm thanh đến Gemini Live...",
            InterpreterEngineType.GoogleCloudPipeline => "Đang nhận dạng giọng nói...",
            InterpreterEngineType.GoogleCloudHybridPipeline => "Đang nhận dạng từng câu bằng Google Speech và xử lý hàng đợi...",
            InterpreterEngineType.GoogleCloudStreamingPipeline => "Đang nghe liên tục bằng Google Streaming STT...",
            InterpreterEngineType.GoogleCloudAdvancedHybridPipeline => "Đang nhận dạng song song bằng Hybrid nâng cao...",
            InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline => "Đang nhận dạng bằng Hybrid thích ứng...",
            InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline => "Đang transcript; tắt micro vật lý để chốt và dịch...",
            _ => "Đang xử lý..."
        };

    private static string GetPlaybackStatus(SupportedLanguage targetLanguage)
        => targetLanguage switch
        {
            SupportedLanguage.Vietnamese => "Đang phát bản dịch tiếng Việt ra loa phòng họp...",
            SupportedLanguage.Korean => "Đang phát bản dịch tiếng Hàn đến tai nghe quản lý...",
            _ => "Đang phát bản dịch..."
        };

    private bool CanUseSynthesisService(ISpeechSynthesisService synthesisService)
        => synthesisService switch
        {
            GoogleCloudSpeechSynthesisService => _googlePipeline.IsInitialized,
            GeminiApiClient => !string.IsNullOrWhiteSpace(Settings.Gemini.ApiKey),
            _ => false
        };

    private static bool IsSafeForPlayback(TranslationResult result)
        => result.SourceLanguage is SupportedLanguage.Vietnamese or SupportedLanguage.Korean
            && result.TargetLanguage is SupportedLanguage.Vietnamese or SupportedLanguage.Korean
            && result.TargetLanguage == LanguageHelper.GetTargetLanguage(result.SourceLanguage)
            && !string.IsNullOrWhiteSpace(result.OriginalText)
            && !string.IsNullOrWhiteSpace(result.TranslatedText);

    private static void CopyEngineResult(InterpreterResult? source, TranslationResult target)
    {
        if (source is null)
        {
            return;
        }

        target.Timestamp = source.Timestamp;
        target.Engine = source.Engine;
        target.SourceLanguage = source.SourceLanguage;
        target.TargetLanguage = source.TargetLanguage;
        target.OriginalText = source.OriginalText;
        target.TranslatedText = source.TranslatedText;
        target.Confidence = source.Confidence;
        target.RecognitionMilliseconds = source.RecognitionMilliseconds;
        target.TranslationMilliseconds = source.TranslationMilliseconds;
        target.AiProcessingMilliseconds = source.AiProcessingMilliseconds;
        target.TotalMilliseconds = source.TotalMilliseconds;
        target.Success = source.Success;
        target.ErrorMessage = source.ErrorMessage;
    }

    private static string ToUserMessage(Exception ex)
    {
        if (ex.Message.Contains("Google Cloud", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("API", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("credential", StringComparison.OrdinalIgnoreCase))
        {
            return "Không thể kết nối đến dịch vụ Google Cloud. Vui lòng kiểm tra kết nối Internet và cấu hình Google Cloud.";
        }

        if (ex.Message.Contains("tai nghe", StringComparison.OrdinalIgnoreCase))
        {
            return "Không tìm thấy tai nghe quản lý Hàn Quốc. Vui lòng kết nối lại thiết bị và chọn \"Làm mới thiết bị\".";
        }

        if (ex.Message.Contains("loa phòng họp", StringComparison.OrdinalIgnoreCase))
        {
            return "Không tìm thấy loa phòng họp. Hệ thống đã dừng phát âm thanh để tránh phát nhầm thiết bị.";
        }

        if (ex.Message.Contains("microphone", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("thiết bị", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("device", StringComparison.OrdinalIgnoreCase))
        {
            return "Không tìm thấy thiết bị âm thanh đã chọn.";
        }

        return "Đã xảy ra lỗi trong quá trình phiên dịch.";
    }

    private sealed class AdvancedLiveTranscriptCandidate
    {
        private string _finalizedText = string.Empty;
        private string _interimText = string.Empty;
        private int _detectedLanguageMatchCount;
        private float? _bestConfidence;

        public AdvancedLiveTranscriptCandidate(SupportedLanguage language)
        {
            Language = language;
        }

        public SupportedLanguage Language { get; }

        public string Text => MergeAdvancedStreamingText(_finalizedText, _interimText);

        public int WordCount => CountAdvancedTranscriptWords(Text);

        public int UpdateCount { get; private set; }

        public bool HasFinalResult { get; private set; }

        public void Update(
            string text,
            SupportedLanguage detectedLanguage,
            float? confidence,
            bool isFinal)
        {
            UpdateCount++;
            if (detectedLanguage == Language)
            {
                _detectedLanguageMatchCount++;
            }

            if (confidence.HasValue
                && (!_bestConfidence.HasValue || confidence.Value > _bestConfidence.Value))
            {
                _bestConfidence = confidence;
            }

            if (isFinal)
            {
                _finalizedText = MergeAdvancedStreamingText(_finalizedText, text);
                _interimText = string.Empty;
                HasFinalResult = true;
                return;
            }

            _interimText = PreferMoreCompleteAdvancedText(_interimText, text);
        }

        public bool HasEnoughEvidence(bool forceDecision)
        {
            if (Language == SupportedLanguage.Korean)
            {
                var minimumWords = forceDecision ? 2 : 3;
                var minimumHangul = forceDecision ? 3 : 5;
                return WordCount >= minimumWords && CountHangulCharacters(Text) >= minimumHangul;
            }

            if (Language == SupportedLanguage.Vietnamese)
            {
                var minimumWords = forceDecision ? 3 : 4;
                return WordCount >= minimumWords
                    && (forceDecision
                        || _detectedLanguageMatchCount >= 2
                        || ContainsVietnameseDiacritic(Text)
                        || HasFinalResult);
            }

            return false;
        }

        public double GetLanguageScore()
        {
            var score = Math.Min(WordCount, 10) * 0.55
                + Math.Min(UpdateCount, 8) * 0.35
                + Math.Min(_detectedLanguageMatchCount, 4) * 0.8
                + (_bestConfidence ?? 0) * 3.0
                + (HasFinalResult ? 1.5 : 0);
            if (Language == SupportedLanguage.Korean && CountHangulCharacters(Text) >= 5)
            {
                score += 1.5;
            }
            else if (Language == SupportedLanguage.Vietnamese && ContainsVietnameseDiacritic(Text))
            {
                score += 1.5;
            }

            return score;
        }

        private static int CountHangulCharacters(string text)
        {
            var count = 0;
            foreach (var character in text)
            {
                if (character >= 0xAC00 && character <= 0xD7AF)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private sealed class AdvancedTranscriptAggregate
    {
        private double _confidenceTotal;
        private int _confidenceCount;

        private AdvancedTranscriptAggregate(AdvancedRecognitionOutcome first)
        {
            var transcript = first.Transcript
                ?? throw new ArgumentException("A completed transcript is required.", nameof(first));
            SequenceNumber = transcript.SequenceNumber;
            CreatedAt = transcript.CreatedAt;
            SourceLanguage = transcript.SourceLanguage;
            TargetLanguage = transcript.TargetLanguage;
            Text = transcript.Text.Trim();
            RecognitionMilliseconds = transcript.RecognitionMilliseconds;
            QueueWaitMilliseconds = transcript.QueueWaitMilliseconds;
            StartedAtUtc = DateTime.UtcNow;
            FragmentCount = 1;
            AddConfidence(transcript.Confidence);
        }

        public long SequenceNumber { get; }

        public DateTime CreatedAt { get; }

        public SupportedLanguage SourceLanguage { get; }

        public SupportedLanguage TargetLanguage { get; }

        public string Text { get; private set; }

        public double RecognitionMilliseconds { get; private set; }

        public double QueueWaitMilliseconds { get; private set; }

        public DateTime StartedAtUtc { get; }

        public int FragmentCount { get; private set; }

        public static AdvancedTranscriptAggregate Start(AdvancedRecognitionOutcome first) => new(first);

        public void Append(AdvancedRecognitionOutcome next)
        {
            var transcript = next.Transcript
                ?? throw new ArgumentException("A completed transcript is required.", nameof(next));
            Text = JoinTranscriptFragments(Text, transcript.Text);
            RecognitionMilliseconds += transcript.RecognitionMilliseconds;
            QueueWaitMilliseconds = Math.Max(QueueWaitMilliseconds, transcript.QueueWaitMilliseconds);
            FragmentCount++;
            AddConfidence(transcript.Confidence);
        }

        public TranscriptUtterance ToTranscript()
            => new()
            {
                SequenceNumber = SequenceNumber,
                CreatedAt = CreatedAt,
                Text = Text,
                SourceLanguage = SourceLanguage,
                TargetLanguage = TargetLanguage,
                Confidence = _confidenceCount == 0 ? null : (float)(_confidenceTotal / _confidenceCount),
                RecognitionMilliseconds = RecognitionMilliseconds,
                QueueWaitMilliseconds = QueueWaitMilliseconds
            };

        private void AddConfidence(float? confidence)
        {
            if (!confidence.HasValue)
            {
                return;
            }

            _confidenceTotal += confidence.Value;
            _confidenceCount++;
        }

        private static string JoinTranscriptFragments(string current, string continuation)
        {
            var left = current.TrimEnd();
            var right = continuation.Trim();
            if (string.IsNullOrWhiteSpace(left))
            {
                return right;
            }

            if (string.IsNullOrWhiteSpace(right))
            {
                return left;
            }

            if (left.EndsWith('.') || left.EndsWith(',') || left.EndsWith(';') || left.EndsWith(':'))
            {
                left = left.TrimEnd('.', ',', ';', ':').TrimEnd();
                return $"{left}, {right}";
            }

            return $"{left} {right}";
        }
    }

    private sealed class AdvancedRecognitionOutcome
    {
        public required AudioUtterance Utterance { get; init; }

        public TranscriptUtterance? Transcript { get; init; }

        public string ErrorMessage { get; init; } = string.Empty;

        public string RecognizedText { get; init; } = string.Empty;

        public float? Confidence { get; init; }

        public double QueueWaitMilliseconds { get; init; }

        public double RecognitionMilliseconds { get; init; }

        public static AdvancedRecognitionOutcome Completed(AudioUtterance utterance, TranscriptUtterance transcript)
            => new()
            {
                Utterance = utterance,
                Transcript = transcript,
                QueueWaitMilliseconds = transcript.QueueWaitMilliseconds,
                RecognitionMilliseconds = transcript.RecognitionMilliseconds,
                RecognizedText = transcript.Text,
                Confidence = transcript.Confidence
            };

        public static AdvancedRecognitionOutcome Failed(
            AudioUtterance utterance,
            string errorMessage,
            double queueWaitMilliseconds,
            double recognitionMilliseconds = 0,
            string recognizedText = "",
            float? confidence = null)
            => new()
            {
                Utterance = utterance,
                ErrorMessage = errorMessage,
                QueueWaitMilliseconds = queueWaitMilliseconds,
                RecognitionMilliseconds = recognitionMilliseconds,
                RecognizedText = recognizedText,
                Confidence = confidence
            };
    }
}

public sealed class InterpreterStateChangedEventArgs : EventArgs
{
    public InterpreterStateChangedEventArgs(InterpreterState state, string message)
    {
        State = state;
        Message = message;
    }

    public InterpreterState State { get; }

    public string Message { get; }
}

public sealed class InterpreterContentPreviewEventArgs : EventArgs
{
    public InterpreterContentPreviewEventArgs(
        string title,
        string content,
        bool? isTranslation = null,
        bool isInterim = true,
        SupportedLanguage language = SupportedLanguage.Unknown)
    {
        Title = title;
        Content = content;
        IsTranslation = isTranslation ?? IsTranslationTitle(title);
        IsInterim = isInterim;
        Language = language;
    }

    public string Title { get; }

    public string Content { get; }

    public bool IsTranslation { get; }

    public bool IsInterim { get; }

    public SupportedLanguage Language { get; }

    private static bool IsTranslationTitle(string title)
        => title.Contains("dịch", StringComparison.OrdinalIgnoreCase)
            || title.Contains("translated", StringComparison.OrdinalIgnoreCase)
            || title.Contains("번역", StringComparison.OrdinalIgnoreCase);
}

public sealed class UtteranceQueueStatusEventArgs : EventArgs
{
    public UtteranceQueueStatusEventArgs(long? processingSequence, int pendingCount, int pendingPlaybackCount = 0)
    {
        ProcessingSequence = processingSequence;
        PendingCount = pendingCount;
        PendingPlaybackCount = pendingPlaybackCount;
    }

    public long? ProcessingSequence { get; }

    public int PendingCount { get; }

    public int PendingPlaybackCount { get; }
}
