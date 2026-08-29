using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class DeepgramSttService : IAsyncDisposable
{
    private const string ListenEndpoint = "wss://api.deepgram.com/v1/listen";
    private readonly DeepgramSettings _settings;
    private readonly AppLogger _logger;
    private readonly object _transcriptSyncRoot = new();
    private readonly StringBuilder _finalTranscriptBuffer = new();
    private readonly Dictionary<SupportedLanguage, int> _finalLanguageVotes = new();
    private string _finalRawLanguageCode = string.Empty;
    private ClientWebSocket? _socket;
    private Channel<byte[]>? _audioChannel;
    private CancellationTokenSource? _sessionCts;
    private Task? _sendTask;
    private Task? _receiveTask;

    public DeepgramSttService(DeepgramSettings settings, AppLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public event EventHandler<StreamingTranscriptEventArgs>? InterimTranscriptReceived;

    public event EventHandler<StreamingTranscriptEventArgs>? FinalTranscriptReceived;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<Exception>? Failed;

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return;
        }

        EnsureApiKey();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _audioChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("Authorization", $"Token {_settings.ApiKey.Trim()}");
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        try
        {
            await _socket.ConnectAsync(BuildUri(), _sessionCts.Token).ConfigureAwait(false);
            _sendTask = Task.Run(() => SendLoopAsync(_sessionCts.Token), CancellationToken.None);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token), CancellationToken.None);
            StatusChanged?.Invoke(this, "Đã kết nối Deepgram realtime STT.");
            _logger.Info("[Deepgram STT] Da ket noi realtime websocket.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            throw ToUserException(ex);
        }
    }

    public void EnqueueAudio(byte[] audioData)
    {
        if (_audioChannel is null || !IsConnected)
        {
            return;
        }

        _audioChannel.Writer.TryWrite(audioData);
    }

    public async Task StopAsync()
    {
        _audioChannel?.Writer.TryComplete();
        _sessionCts?.Cancel();

        if (_socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session stopped", CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }

        await AwaitTaskAsync(_sendTask).ConfigureAwait(false);
        await AwaitTaskAsync(_receiveTask).ConfigureAwait(false);

        _socket?.Dispose();
        _socket = null;
        _sessionCts?.Dispose();
        _sessionCts = null;
        _audioChannel = null;
        _sendTask = null;
        _receiveTask = null;

        lock (_transcriptSyncRoot)
        {
            _finalTranscriptBuffer.Clear();
        }
    }

    public async ValueTask DisposeAsync()
        => await StopAsync().ConfigureAwait(false);

    private Uri BuildUri()
    {
        var query = new Dictionary<string, string>
        {
            ["model"] = string.IsNullOrWhiteSpace(_settings.Model) ? "nova-2" : _settings.Model.Trim(),
            ["language"] = NormalizeLanguageOption(_settings.Language),
            ["encoding"] = "linear16",
            ["sample_rate"] = "16000",
            ["channels"] = "1",
            ["punctuate"] = _settings.Punctuate ? "true" : "false",
            ["interim_results"] = _settings.InterimResults ? "true" : "false",
            ["smart_format"] = _settings.SmartFormat ? "true" : "false",
            ["endpointing"] = Math.Clamp(_settings.EndpointingMs, 100, 3000).ToString(),
            ["utterance_end_ms"] = Math.Clamp(_settings.EndpointingMs, 100, 3000).ToString()
        };

        var queryString = string.Join("&", query.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
        return new Uri($"{ListenEndpoint}?{queryString}");
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        if (_audioChannel is null || _socket is null)
        {
            return;
        }

        try
        {
            await foreach (var audioData in _audioChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open)
                {
                    break;
                }

                await _socket.SendAsync(new ArraySegment<byte>(audioData), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PublishFailure(ex);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            return;
        }

        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var json = await ReceiveTextMessageAsync(_socket, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    HandleMessage(json);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PublishFailure(ex);
        }
    }

    private void HandleMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("type", out var typeElement)
            && string.Equals(typeElement.GetString(), "UtteranceEnd", StringComparison.OrdinalIgnoreCase))
        {
            FlushFinalTranscript();
            return;
        }

        if (!TryReadTranscript(root, out var transcript) || string.IsNullOrWhiteSpace(transcript.Text))
        {
            return;
        }

        if (!transcript.IsFinal)
        {
            InterimTranscriptReceived?.Invoke(
                this,
                new StreamingTranscriptEventArgs(
                    transcript.Text,
                    transcript.Language,
                    transcript.Confidence,
                    transcript.RawLanguageCode));
            return;
        }

        lock (_transcriptSyncRoot)
        {
            if (_finalTranscriptBuffer.Length > 0)
            {
                _finalTranscriptBuffer.Append(' ');
            }

            _finalTranscriptBuffer.Append(transcript.Text);
            AddLanguageVote(transcript.Language);
            if (!string.IsNullOrWhiteSpace(transcript.RawLanguageCode))
            {
                _finalRawLanguageCode = transcript.RawLanguageCode;
            }
        }

        if (transcript.SpeechFinal)
        {
            FlushFinalTranscript();
        }
    }

    private void FlushFinalTranscript()
    {
        string text;
        SupportedLanguage language;
        string rawLanguageCode;
        lock (_transcriptSyncRoot)
        {
            text = _finalTranscriptBuffer.ToString().Trim();
            language = ResolveBufferedLanguage();
            rawLanguageCode = _finalRawLanguageCode;
            _finalTranscriptBuffer.Clear();
            _finalLanguageVotes.Clear();
            _finalRawLanguageCode = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            FinalTranscriptReceived?.Invoke(this, new StreamingTranscriptEventArgs(text, language, null, rawLanguageCode));
        }
    }

    private static bool TryReadTranscript(
        JsonElement root,
        out (string Text, bool IsFinal, bool SpeechFinal, SupportedLanguage Language, float? Confidence, string RawLanguageCode) transcript)
    {
        transcript = default;
        if (!root.TryGetProperty("channel", out var channel)
            || !channel.TryGetProperty("alternatives", out var alternatives)
            || alternatives.ValueKind != JsonValueKind.Array
            || alternatives.GetArrayLength() == 0)
        {
            return false;
        }

        var alternative = alternatives[0];
        var text = alternative.TryGetProperty("transcript", out var transcriptElement)
            ? transcriptElement.GetString() ?? string.Empty
            : string.Empty;
        var isFinal = root.TryGetProperty("is_final", out var isFinalElement) && isFinalElement.GetBoolean();
        var speechFinal = root.TryGetProperty("speech_final", out var speechFinalElement) && speechFinalElement.GetBoolean();
        var confidence = alternative.TryGetProperty("confidence", out var confidenceElement)
            && confidenceElement.ValueKind == JsonValueKind.Number
            && confidenceElement.TryGetSingle(out var confidenceValue)
                ? confidenceValue
                : (float?)null;
        var rawLanguageCode = ReadDominantLanguageCode(alternative);
        transcript = (text, isFinal, speechFinal, LanguageHelper.ParseLanguage(rawLanguageCode), confidence, rawLanguageCode);
        return true;
    }

    private void AddLanguageVote(SupportedLanguage language)
    {
        if (language == SupportedLanguage.Unknown)
        {
            return;
        }

        _finalLanguageVotes.TryGetValue(language, out var count);
        _finalLanguageVotes[language] = count + 1;
    }

    private SupportedLanguage ResolveBufferedLanguage()
        => _finalLanguageVotes
            .OrderByDescending(item => item.Value)
            .Select(item => item.Key)
            .FirstOrDefault();

    private static string NormalizeLanguageOption(string? language)
        => "multi";

    private static string ReadDominantLanguageCode(JsonElement alternative)
    {
        var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (alternative.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
        {
            foreach (var word in words.EnumerateArray())
            {
                if (word.TryGetProperty("language", out var languageElement))
                {
                    AddLanguageVote(votes, languageElement.GetString());
                }
            }
        }

        if (votes.Count == 0
            && alternative.TryGetProperty("languages", out var languages)
            && languages.ValueKind == JsonValueKind.Array)
        {
            foreach (var language in languages.EnumerateArray())
            {
                AddLanguageVote(votes, language.GetString());
            }
        }

        return votes
            .OrderByDescending(item => item.Value)
            .Select(item => item.Key)
            .FirstOrDefault() ?? string.Empty;
    }

    private static void AddLanguageVote(Dictionary<string, int> votes, string? languageCode)
    {
        var language = LanguageHelper.ParseLanguage(languageCode);
        if (language == SupportedLanguage.Unknown || string.IsNullOrWhiteSpace(languageCode))
        {
            return;
        }

        var normalized = languageCode.Trim().ToLowerInvariant();
        votes.TryGetValue(normalized, out var count);
        votes[normalized] = count + 1;
    }

    private static async Task<string> ReceiveTextMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Deepgram WebSocket đã đóng kết nối.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void EnsureApiKey()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException("Chưa cấu hình API Key Deepgram.");
        }
    }

    private void PublishFailure(Exception exception)
    {
        var userException = ToUserException(exception);
        _logger.Error("[Deepgram STT] Loi realtime websocket.", userException);
        Failed?.Invoke(this, userException);
    }

    private static Exception ToUserException(Exception exception)
    {
        if (exception is InvalidOperationException)
        {
            return exception;
        }

        if (exception is WebSocketException)
        {
            return new InvalidOperationException("Mất kết nối Deepgram realtime STT. Vui lòng kiểm tra Internet/API Key và bắt đầu lại phiên.", exception);
        }

        return new InvalidOperationException($"Không thể kết nối Deepgram realtime STT: {exception.Message}", exception);
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
}
