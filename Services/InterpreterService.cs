using System.Diagnostics;
using System.Threading.Channels;
using Grpc.Core;
using MeetingInterpreter.Models;
using NAudio.Wave;

namespace MeetingInterpreter.Services;

public sealed class InterpreterService : IDisposable
{
    private readonly AudioService _audioService;
    private readonly GoogleTranslatePipeline _googlePipeline;
    private readonly GeminiApiClient _geminiClient;
    private readonly GeminiLiveSession _geminiLiveSession;
    private readonly GoogleStreamingSttService _googleStreamingSttService;
    private readonly DeepgramSttService _deepgramSttService;
    private readonly InterpreterEngineFactory _engineFactory;
    private readonly VoiceActivityDetector _vad;
    private readonly AppLogger _logger;
    private readonly object _stateSyncRoot = new();
    private Channel<AudioUtterance>? _utteranceChannel;
    private Channel<TranscriptUtterance>? _translationQueue;
    private Channel<TtsItem>? _ttsQueue;
    private Channel<PlaybackItem>? _playbackQueue;
    private CancellationTokenSource? _sessionCts;
    private Task? _workerTask;
    private Task? _translationWorkerTask;
    private Task? _ttsWorkerTask;
    private Task? _playbackWorkerTask;
    private Task? _streamingSupervisorTask;
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
        _deepgramSttService = deepgramSttService;
        _engineFactory = engineFactory;
        _vad = vad;
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

        ValidateDevices(inputDevice, output1Device, output2Device, credentialPath);
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
        _isRunning = false;
        _utteranceChannel?.Writer.TryComplete();
        _translationQueue?.Writer.TryComplete();
        _ttsQueue?.Writer.TryComplete();
        _playbackQueue?.Writer.TryComplete();
        _sessionCts?.Cancel();
        await _geminiLiveSession.StopAsync().ConfigureAwait(false);
        await _googleStreamingSttService.StopAsync().ConfigureAwait(false);
        await _deepgramSttService.StopAsync().ConfigureAwait(false);
        _audioService.StopPcmStreamPlayback();
        _audioService.StopActivePlayback();
        _audioService.StopCapture();
        _vad.Reset();

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

        _sessionCts?.Dispose();
        _sessionCts = null;
        _workerTask = null;
        _translationWorkerTask = null;
        _ttsWorkerTask = null;
        _playbackWorkerTask = null;
        _streamingSupervisorTask = null;
        _utteranceChannel = null;
        _translationQueue = null;
        _ttsQueue = null;
        _playbackQueue = null;
        _suppressMicrophoneProcessing = false;
        _livePlaybackSuppressCts?.Cancel();
        _livePlaybackSuppressCts?.Dispose();
        _livePlaybackSuppressCts = null;
        ResetLiveTurn();
        _pendingUtteranceCount = 0;
        _pendingTtsCount = 0;
        _pendingPlaybackCount = 0;
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
        _deepgramSttService.InterimTranscriptReceived -= OnDeepgramInterimTranscriptReceived;
        _deepgramSttService.FinalTranscriptReceived -= OnDeepgramFinalTranscriptReceived;
        _deepgramSttService.Failed -= OnDeepgramFailed;
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

        if (!_utteranceChannel.Writer.TryWrite(utterance))
        {
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

        ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Nội dung nói", args.Text));
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
        if (!_isRunning || Settings.EngineType != InterpreterEngineType.GoogleCloudStreamingPipeline)
        {
            return;
        }

        UpdateRuntimeState(state =>
        {
            state.IsSttConnected = true;
            state.IsReconnectingStt = false;
            state.IsListening = true;
        });
        ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Đang nghe", args.Text));
    }

    private void OnGoogleStreamingFinalTranscriptReceived(object? sender, StreamingTranscriptEventArgs args)
    {
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

    private void OnGoogleStreamingRecoverableFailure(object? sender, Exception exception)
    {
        _logger.Error("[Google Streaming STT] Loi tam thoi, dang tu ket noi lai.", exception);
        if (_isRunning && Settings.EngineType == InterpreterEngineType.GoogleCloudStreamingPipeline)
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
        RequestGoogleStreamingRestart($"Google Streaming STT gap loi nang: {exception.Message}");
    }

    private void OnGoogleStreamingAudioQueueOverloaded(object? sender, EventArgs args)
    {
        if (!_isRunning || Settings.EngineType != InterpreterEngineType.GoogleCloudStreamingPipeline)
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
            RequestGoogleStreamingRestart("Google Streaming STT bi qua tai audio queue lien tuc.");
        }
    }

    private void RequestGoogleStreamingRestart(string reason)
    {
        if (!_isRunning || Settings.EngineType != InterpreterEngineType.GoogleCloudStreamingPipeline)
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
                || Settings.EngineType != InterpreterEngineType.GoogleCloudStreamingPipeline
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
                && Settings.EngineType == InterpreterEngineType.GoogleCloudStreamingPipeline
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

                    _vad.Reset();
                    await _googleStreamingSttService.StartAsync(cancellationToken).ConfigureAwait(false);
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
        ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Nội dung nói", $"#{sequence} {utterance.Text}"));

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
            ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Nội dung nói", result.OriginalText));

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
            ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Nội dung dịch", result.TranslatedText));

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

                if (Settings.EngineType == InterpreterEngineType.GoogleCloudHybridPipeline)
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
            ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Noi dung noi", $"#{utterance.SequenceNumber} {recognition.Text}"));

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
            Confidence = utterance.Confidence
        };

        try
        {
            PublishQueueStatus(utterance.SequenceNumber);
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
            result.TranslatedText = await RunWithRetryAsync(
                token => _googlePipeline.TranslateTextAsync(
                    result.OriginalText,
                    result.SourceLanguage,
                    result.TargetLanguage,
                    token),
                TimeSpan.FromSeconds(Math.Clamp(Settings.SpeechRecognition.TranslationTimeoutSeconds, 10, 30)),
                Settings.SpeechRecognition.ApiRetryCount,
                $"Translation #{utterance.SequenceNumber}",
                cancellationToken).ConfigureAwait(false);
            stage.Stop();
            result.TranslationMilliseconds = stage.Elapsed.TotalMilliseconds;
            Interlocked.Increment(ref _metrics.TotalTranslated);
            SetUtteranceStage(utterance.SequenceNumber, UtteranceStage.Translated);
            _logger.Info($"[Streaming #{utterance.SequenceNumber}] Translation Completed.");
            ContentPreviewChanged?.Invoke(this, new InterpreterContentPreviewEventArgs("Nội dung dịch", $"#{utterance.SequenceNumber} {result.TranslatedText}"));

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
            UpdateRuntimeState(state => state.IsSynthesizing = true);
            SetUtteranceStage(item.SequenceNumber, UtteranceStage.Synthesizing);
            SetState(InterpreterState.Synthesizing, $"Đang tạo giọng nói: Câu #{item.SequenceNumber}");
            _logger.Info($"[Streaming #{item.SequenceNumber}] TTS Started.");
            var stage = Stopwatch.StartNew();
            var ttsAudio = await RunWithRetryAsync(
                token => _engineFactory
                    .GetSpeechSynthesisService(item.Result.Engine)
                    .SynthesizeAsync(item.TranslatedText, item.TargetLanguage, token),
                TimeSpan.FromSeconds(Math.Clamp(Settings.SpeechRecognition.TtsTimeoutSeconds, 10, 30)),
                Settings.SpeechRecognition.ApiRetryCount,
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
            UpdateRuntimeState(state => state.IsPlaying = true);
            SetUtteranceStage(item.SequenceNumber, UtteranceStage.Playing);
            SetState(InterpreterState.Playing, $"Đang phát: Câu #{item.SequenceNumber}");
            _logger.Info($"[Streaming #{item.SequenceNumber}] Playback Started.");
            await PlayTranslatedAudioAsync(item.AudioData, item.TargetLanguage, cancellationToken).ConfigureAwait(false);
            playbackWatch.Stop();

            item.Result.PlaybackPreparationMilliseconds = playbackWatch.Elapsed.TotalMilliseconds;
            item.Result.TotalMilliseconds += playbackWatch.Elapsed.TotalMilliseconds;
            item.Result.Success = true;
            TranslationCompleted?.Invoke(this, item.Result);
            Interlocked.Increment(ref _metrics.TotalPlaybackCompleted);
            SetUtteranceStage(item.SequenceNumber, UtteranceStage.Completed);
            _logger.Info($"[Streaming #{item.SequenceNumber}] Playback Completed.");
            PublishQueueStatus(null);

            if (_isRunning && State != InterpreterState.Error)
            {
                SetState(InterpreterState.Listening, "Đang nghe liên tục bằng Google Streaming Speech-to-Text.");
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

        var stopCaptureDuringRoomSpeakerPlayback = targetLanguage == SupportedLanguage.Vietnamese;
        var suppress = stopCaptureDuringRoomSpeakerPlayback || Settings.SuppressMicDuringHeadsetPlayback;
        if (suppress)
        {
            _suppressMicrophoneProcessing = true;
            _vad.Reset();
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

    private void ValidateDevices(
        AudioDeviceInfo inputDevice,
        AudioDeviceInfo output1Device,
        AudioDeviceInfo output2Device,
        string? credentialPath)
    {
        if (Settings.EngineType is InterpreterEngineType.GoogleCloudPipeline or InterpreterEngineType.GoogleCloudHybridPipeline or InterpreterEngineType.GoogleCloudStreamingPipeline
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

        var inputExists = EnumerateInputDevices().Any(device => device.Id == inputDevice.Id);
        var output1Exists = EnumerateOutputDevices().Any(device => device.Id == output1Device.Id);
        var output2Exists = EnumerateOutputDevices().Any(device => device.Id == output2Device.Id);

        if (!inputExists)
        {
            throw new InvalidOperationException("Không tìm thấy microphone phòng họp.");
        }

        if (!output1Exists)
        {
            throw new InvalidOperationException("Không tìm thấy loa phòng họp.");
        }

        if (!output2Exists)
        {
            throw new InvalidOperationException("Không tìm thấy tai nghe quản lý Hàn Quốc.");
        }
    }

    private async Task EnsureSelectedEngineInitializedAsync(
        InterpreterEngineType engineType,
        string? credentialPath,
        CancellationToken cancellationToken)
    {
        if (engineType is InterpreterEngineType.GoogleCloudPipeline or InterpreterEngineType.GoogleCloudHybridPipeline or InterpreterEngineType.GoogleCloudStreamingPipeline)
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
        => engineType is InterpreterEngineType.GoogleCloudHybridPipeline or InterpreterEngineType.GoogleCloudStreamingPipeline;

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
            _ => "Không xác định"
        };

    private static string GetEngineProcessingStatus(InterpreterEngineType engineType)
        => engineType switch
        {
            InterpreterEngineType.Gemini25Pro => "Đang stream âm thanh đến Gemini Live...",
            InterpreterEngineType.GoogleCloudPipeline => "Đang nhận dạng giọng nói...",
            InterpreterEngineType.GoogleCloudHybridPipeline => "Đang nhận dạng từng câu bằng Google Speech và xử lý hàng đợi...",
            InterpreterEngineType.GoogleCloudStreamingPipeline => "Đang nghe liên tục bằng Google Streaming STT...",
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
    public InterpreterContentPreviewEventArgs(string title, string content)
    {
        Title = title;
        Content = content;
    }

    public string Title { get; }

    public string Content { get; }
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
