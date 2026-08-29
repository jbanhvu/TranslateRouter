using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class GeminiLiveSession : IAsyncDisposable
{
    private const string WebSocketEndpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    private readonly GeminiSettings _settings;
    private readonly AppLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private Channel<byte[]>? _audioChannel;
    private CancellationTokenSource? _sessionCts;
    private Task? _sendTask;
    private Task? _receiveTask;

    public GeminiLiveSession(GeminiSettings settings, AppLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public event EventHandler<byte[]>? AudioOutputReceived;

    public event EventHandler<string>? InputTranscriptionReceived;

    public event EventHandler<string>? OutputTranscriptionReceived;

    public event EventHandler? TurnCompleted;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<Exception>? Failed;

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        await using var session = new GeminiLiveSession(_settings, _logger);
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        await session.StopAsync().ConfigureAwait(false);
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
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        var uri = new Uri($"{WebSocketEndpoint}?key={Uri.EscapeDataString(_settings.ApiKey.Trim())}");
        try
        {
            await _socket.ConnectAsync(uri, _sessionCts.Token).ConfigureAwait(false);
            await SendJsonAsync(CreateSetupMessage(), _sessionCts.Token).ConfigureAwait(false);
            await WaitForSetupCompleteAsync(_sessionCts.Token).ConfigureAwait(false);

            _sendTask = Task.Run(() => SendLoopAsync(_sessionCts.Token), CancellationToken.None);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token), CancellationToken.None);
            StatusChanged?.Invoke(this, "Đã kết nối Gemini Live API.");
            _logger.Info("[Gemini Live] Da ket noi WebSocket.");
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
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        if (_audioChannel is null)
        {
            return;
        }

        try
        {
            await foreach (var audioData in _audioChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var message = new
                {
                    realtimeInput = new
                    {
                        audio = new
                        {
                            data = Convert.ToBase64String(audioData),
                            mimeType = "audio/pcm;rate=16000"
                        }
                    }
                };

                await SendJsonAsync(message, cancellationToken).ConfigureAwait(false);
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
        try
        {
            while (_socket is { State: WebSocketState.Open } socket && !cancellationToken.IsCancellationRequested)
            {
                var json = await ReceiveTextMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                HandleServerMessage(json);
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

    private void HandleServerMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(ReadErrorMessage(error));
        }

        if (root.TryGetProperty("goAway", out _))
        {
            StatusChanged?.Invoke(this, "Gemini Live API sắp đóng kết nối. Vui lòng khởi động lại phiên nếu cần.");
            return;
        }

        if (!root.TryGetProperty("serverContent", out var serverContent))
        {
            return;
        }

        if (serverContent.TryGetProperty("interrupted", out var interrupted) && interrupted.GetBoolean())
        {
            StatusChanged?.Invoke(this, "Gemini Live đã ngắt lượt phát hiện tại.");
        }

        if (serverContent.TryGetProperty("inputTranscription", out var inputTranscription))
        {
            var text = ReadTranscriptionText(inputTranscription);
            if (!string.IsNullOrWhiteSpace(text))
            {
                InputTranscriptionReceived?.Invoke(this, text);
            }
        }

        if (serverContent.TryGetProperty("outputTranscription", out var outputTranscription))
        {
            var text = ReadTranscriptionText(outputTranscription);
            if (!string.IsNullOrWhiteSpace(text))
            {
                OutputTranscriptionReceived?.Invoke(this, text);
            }
        }

        if (serverContent.TryGetProperty("modelTurn", out var modelTurn)
            && modelTurn.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("inlineData", out var inlineData)
                    && inlineData.TryGetProperty("data", out var dataElement))
                {
                    var base64 = dataElement.GetString();
                    if (!string.IsNullOrWhiteSpace(base64))
                    {
                        AudioOutputReceived?.Invoke(this, Convert.FromBase64String(base64));
                    }
                }
            }
        }

        if (serverContent.TryGetProperty("turnComplete", out var turnComplete) && turnComplete.GetBoolean())
        {
            TurnCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task WaitForSetupCompleteAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            throw new InvalidOperationException("Gemini Live WebSocket chưa được tạo.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextMessageAsync(_socket, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("setupComplete", out _))
            {
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(ReadErrorMessage(error));
            }
        }
    }

    private async Task SendJsonAsync(object message, CancellationToken cancellationToken)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Gemini Live WebSocket chưa kết nối.");
        }

        var json = JsonSerializer.Serialize(message, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
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
                throw new WebSocketException("Gemini Live WebSocket đã đóng kết nối.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private object CreateSetupMessage()
        => new
        {
            setup = new
            {
                model = $"models/{GetLiveModelName()}",
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" },
                    speechConfig = new
                    {
                        voiceConfig = new
                        {
                            prebuiltVoiceConfig = new
                            {
                                voiceName = string.IsNullOrWhiteSpace(_settings.TtsVoice) ? "Kore" : _settings.TtsVoice.Trim()
                            }
                        }
                    }
                },
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new { text = BuildLiveSystemInstruction() }
                    }
                },
                inputAudioTranscription = new { },
                outputAudioTranscription = new { },
                realtimeInputConfig = new
                {
                    automaticActivityDetection = new
                    {
                        silenceDurationMs = 1000
                    }
                }
            }
        };

    private string GetLiveModelName()
    {
        var model = string.IsNullOrWhiteSpace(_settings.Model)
            ? "gemini-3.1-flash-live-preview"
            : _settings.Model.Trim();

        return model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? model["models/".Length..]
            : model;
    }

    private static string BuildLiveSystemInstruction()
        => """
You are a real-time meeting interpreter for Vietnamese employees and Korean managers.

Listen to the user's spoken audio.
If the user speaks Vietnamese, respond only with the Korean translation.
If the user speaks Korean, respond only with the Vietnamese translation.
Do not answer questions, do not explain, do not summarize, and do not add content.
Preserve names, company names, quantities, dates, product codes, PO, LOT, ERP, MES, SCM, and manufacturing terminology.
If the audio is unclear or not Vietnamese/Korean, stay silent.
Speak naturally for a business/manufacturing meeting.
""";

    private void PublishFailure(Exception exception)
    {
        var userException = ToUserException(exception);
        _logger.Error("[Gemini Live] Loi WebSocket.", userException);
        Failed?.Invoke(this, userException);
    }

    private Exception ToUserException(Exception exception)
    {
        if (exception is InvalidOperationException)
        {
            return exception;
        }

        if (exception is WebSocketException)
        {
            return new InvalidOperationException("Mất kết nối Gemini Live API. Vui lòng kiểm tra Internet/API Key và bắt đầu lại phiên.", exception);
        }

        return new InvalidOperationException($"Không thể kết nối Gemini Live API: {exception.Message}", exception);
    }

    private void EnsureApiKey()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException("Chưa cấu hình API Key Gemini.");
        }
    }

    private static string ReadTranscriptionText(JsonElement element)
    {
        if (element.TryGetProperty("text", out var textElement))
        {
            return textElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ReadErrorMessage(JsonElement error)
    {
        if (error.TryGetProperty("message", out var message))
        {
            return message.GetString() ?? "Gemini Live API trả về lỗi.";
        }

        return "Gemini Live API trả về lỗi.";
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
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }
}
