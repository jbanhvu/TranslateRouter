using System.Threading.Channels;
using Grpc.Core;
using Google.Cloud.Speech.V1;
using Google.Protobuf;
using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class GoogleStreamingSttService : IAsyncDisposable
{
    private readonly GoogleTranslatePipeline _pipeline;
    private readonly AppLogger _logger;
    private readonly SpeechRecognitionSettings _settings;
    private readonly Queue<byte[]> _rollingAudio = new();
    private readonly object _rollingAudioSyncRoot = new();
    private Channel<byte[]>? _vietnamesePrimaryAudioChannel;
    private Channel<byte[]>? _koreanPrimaryAudioChannel;
    private CancellationTokenSource? _sessionCts;
    private Task? _vietnamesePrimarySupervisorTask;
    private Task? _koreanPrimarySupervisorTask;

    public GoogleStreamingSttService(GoogleTranslatePipeline pipeline, AppLogger logger)
    {
        _pipeline = pipeline;
        _logger = logger;
        _settings = pipeline.SpeechSettings;
    }

    public bool IsRunning => _sessionCts is not null && !_sessionCts.IsCancellationRequested;

    public event EventHandler<StreamingTranscriptEventArgs>? InterimTranscriptReceived;

    public event EventHandler<StreamingTranscriptEventArgs>? FinalTranscriptReceived;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<Exception>? RecoverableFailure;

    public event EventHandler<Exception>? FatalFailure;

    public event EventHandler? AudioQueueOverloaded;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        if (!_pipeline.IsInitialized)
        {
            throw new InvalidOperationException("Google Cloud chưa được cấu hình.");
        }

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _vietnamesePrimaryAudioChannel = CreateAudioChannel();
        _koreanPrimaryAudioChannel = CreateAudioChannel();

        _vietnamesePrimarySupervisorTask = Task.Run(
            () => RunSupervisorAsync(_vietnamesePrimaryAudioChannel.Reader, SupportedLanguage.Vietnamese, _sessionCts.Token),
            CancellationToken.None);
        _koreanPrimarySupervisorTask = Task.Run(
            () => RunSupervisorAsync(_koreanPrimaryAudioChannel.Reader, SupportedLanguage.Korean, _sessionCts.Token),
            CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void EnqueueAudio(byte[] audioData)
    {
        if (!IsRunning)
        {
            return;
        }

        AddRollingAudio(audioData);
        var acceptedByVietnameseStream = TryWriteAudio(_vietnamesePrimaryAudioChannel, audioData);
        var acceptedByKoreanStream = TryWriteAudio(_koreanPrimaryAudioChannel, audioData);
        if (!acceptedByVietnameseStream || !acceptedByKoreanStream)
        {
            _logger.Error("[Google Streaming STT] Audio queue dang qua tai.");
            AudioQueueOverloaded?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, "Âm thanh đầu vào đang quá tải, hệ thống có thể chậm hơn tốc độ nói.");
        }
    }

    public async Task StopAsync()
    {
        _vietnamesePrimaryAudioChannel?.Writer.TryComplete();
        _koreanPrimaryAudioChannel?.Writer.TryComplete();
        _sessionCts?.Cancel();

        await AwaitTaskAsync(_vietnamesePrimarySupervisorTask).ConfigureAwait(false);
        await AwaitTaskAsync(_koreanPrimarySupervisorTask).ConfigureAwait(false);
        _vietnamesePrimaryAudioChannel = null;
        _koreanPrimaryAudioChannel = null;
        _vietnamesePrimarySupervisorTask = null;
        _koreanPrimarySupervisorTask = null;
        _sessionCts?.Dispose();
        _sessionCts = null;
        lock (_rollingAudioSyncRoot)
        {
            _rollingAudio.Clear();
        }
    }

    public async ValueTask DisposeAsync()
        => await StopAsync().ConfigureAwait(false);

    private static Channel<byte[]> CreateAudioChannel()
        => Channel.CreateBounded<byte[]>(new BoundedChannelOptions(600)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private static bool TryWriteAudio(Channel<byte[]>? channel, byte[] audioData)
        => channel is not null && channel.Writer.TryWrite(audioData);

    private async Task RunSupervisorAsync(
        ChannelReader<byte[]> audioReader,
        SupportedLanguage primaryLanguage,
        CancellationToken cancellationToken)
    {
        var reconnectDelay = TimeSpan.FromSeconds(1);
        var reconnectAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                reconnectAttempt++;
                _logger.Info($"[Google Streaming STT] {primaryLanguage} primary reconnect attempt {reconnectAttempt}.");
                StatusChanged?.Invoke(this, "Đang kết nối Google Streaming Speech-to-Text...");
                await RunStreamingSessionAsync(audioReader, primaryLanguage, cancellationToken).ConfigureAwait(false);
                reconnectDelay = TimeSpan.FromSeconds(1);
                reconnectAttempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsReconnectable(ex))
            {
                PublishRecoverableFailure(ex, reconnectDelay);
                try
                {
                    await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                reconnectDelay = TimeSpan.FromSeconds(Math.Min(reconnectDelay.TotalSeconds * 2, 10));
            }
            catch (Exception ex)
            {
                PublishFatalFailure(ex);
                break;
            }
        }
    }

    private async Task RunStreamingSessionAsync(
        ChannelReader<byte[]> audioReader,
        SupportedLanguage primaryLanguage,
        CancellationToken cancellationToken)
    {
        using var stream = _pipeline.CreateStreamingRecognizeStream(cancellationToken);
        await stream.WriteAsync(new StreamingRecognizeRequest
        {
            StreamingConfig = _pipeline.CreateStreamingRecognitionConfig(primaryLanguage)
        }).ConfigureAwait(false);

        StatusChanged?.Invoke(this, "Đã kết nối Google Speech.");
        _logger.Info($"[Google Streaming STT] StreamingRecognize connected. Primary={primaryLanguage}.");
        await SendRollingAudioAsync(stream, cancellationToken).ConfigureAwait(false);

        using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sessionDuration = ClampSessionDuration(_settings.MaxStreamingSessionDuration);
        lifetimeCts.CancelAfter(sessionDuration);
        var sendTask = SendLoopAsync(stream, audioReader, lifetimeCts.Token);
        var receiveTask = ReceiveLoopAsync(stream, primaryLanguage, lifetimeCts.Token);

        try
        {
            try
            {
                var completed = await Task.WhenAny(sendTask, receiveTask).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.Info("[Google Streaming STT] Chu dong restart stream truoc gioi han 5 phut.");
                StatusChanged?.Invoke(this, "Đang làm mới kết nối Google Speech...");
            }
        }
        finally
        {
            lifetimeCts.Cancel();
            await DrainStreamTaskAsync(sendTask).ConfigureAwait(false);
            await DrainStreamTaskAsync(receiveTask).ConfigureAwait(false);

            try
            {
                await stream.WriteCompleteAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async Task DrainStreamTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendLoopAsync(
        SpeechClient.StreamingRecognizeStream stream,
        ChannelReader<byte[]> audioReader,
        CancellationToken cancellationToken)
    {
        await foreach (var audioData in audioReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await stream.WriteAsync(new StreamingRecognizeRequest
            {
                AudioContent = ByteString.CopyFrom(audioData)
            }).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(
        SpeechClient.StreamingRecognizeStream stream,
        SupportedLanguage primaryLanguage,
        CancellationToken cancellationToken)
    {
        var responseStream = stream.GetResponseStream();
        while (await responseStream.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var result in responseStream.Current.Results)
            {
                PublishTranscript(result, primaryLanguage);
            }
        }
    }

    private void PublishTranscript(StreamingRecognitionResult result, SupportedLanguage primaryLanguage)
    {
        var alternative = result.Alternatives.FirstOrDefault();
        if (alternative is null)
        {
            return;
        }

        var text = NormalizeTranscript(alternative.Transcript);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (ShouldSkipPrimaryStreamTranscript(primaryLanguage, text))
        {
            _logger.Info($"[Google Streaming STT] Skip transcript from {primaryLanguage} primary stream because text script does not match. Text={text}");
            return;
        }

        var language = LanguageHelper.ParseLanguage(result.LanguageCode);
        float? confidence = alternative.Confidence > 0 ? alternative.Confidence : null;
        var args = new StreamingTranscriptEventArgs(text, language, confidence, result.LanguageCode);

        if (result.IsFinal)
        {
            FinalTranscriptReceived?.Invoke(this, args);
            return;
        }

        InterimTranscriptReceived?.Invoke(this, args);
    }

    private void PublishRecoverableFailure(Exception exception, TimeSpan retryDelay)
    {
        var userException = new InvalidOperationException(
            $"Mất kết nối dịch vụ nhận dạng. Đang tự động kết nối lại sau {retryDelay.TotalSeconds:0}s...",
            exception);
        _logger.Error("[Google Streaming STT] Loi StreamingRecognize.", userException);
        _logger.Info($"[Google Streaming STT] Reconnect scheduled after {retryDelay.TotalSeconds:0}s.");
        RecoverableFailure?.Invoke(this, userException);
    }

    private void PublishFatalFailure(Exception exception)
    {
        var userException = new InvalidOperationException(
            "Google Streaming Speech-to-Text gặp lỗi cấu hình không thể tự phục hồi. Vui lòng kiểm tra Service Account, API và model STT.",
            exception);
        _logger.Error("[Google Streaming STT] Fatal StreamingRecognize error.", userException);
        FatalFailure?.Invoke(this, userException);
    }

    private void AddRollingAudio(byte[] audioData)
    {
        var copy = new byte[audioData.Length];
        Buffer.BlockCopy(audioData, 0, copy, 0, audioData.Length);

        lock (_rollingAudioSyncRoot)
        {
            _rollingAudio.Enqueue(copy);
            var maxBytes = 16000 * 2 * Math.Clamp(_settings.StreamingRestartOverlapMs, 500, 1000) / 1000;
            var totalBytes = _rollingAudio.Sum(item => item.Length);
            while (totalBytes > maxBytes && _rollingAudio.Count > 0)
            {
                totalBytes -= _rollingAudio.Dequeue().Length;
            }
        }
    }

    private async Task SendRollingAudioAsync(SpeechClient.StreamingRecognizeStream stream, CancellationToken cancellationToken)
    {
        byte[][] chunks;
        lock (_rollingAudioSyncRoot)
        {
            chunks = _rollingAudio.ToArray();
        }

        foreach (var chunk in chunks)
        {
            await stream.WriteAsync(new StreamingRecognizeRequest
            {
                AudioContent = ByteString.CopyFrom(chunk)
            }).ConfigureAwait(false);
        }
    }

    private static TimeSpan ClampSessionDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(290);
        }

        return duration > TimeSpan.FromSeconds(290)
            ? TimeSpan.FromSeconds(290)
            : duration;
    }

    private static bool IsReconnectable(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return false;
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
        if (message.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("503", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || message.Contains("stream removed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("closed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("unsupported", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AwaitTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string NormalizeTranscript(string text)
        => string.Join(" ", text.Trim().Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));

    private static bool ShouldSkipPrimaryStreamTranscript(SupportedLanguage primaryLanguage, string text)
        => primaryLanguage switch
        {
            SupportedLanguage.Vietnamese => ContainsHangul(text),
            SupportedLanguage.Korean => !ContainsHangul(text),
            _ => false
        };

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
}
