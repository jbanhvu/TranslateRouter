using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingInterpreter.Models;
using NAudio.Wave;

namespace MeetingInterpreter.Services;

public sealed class GeminiApiClient : ISpeechSynthesisService, IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly GeminiSettings _settings;
    private readonly AppLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GeminiApiClient(GeminiSettings settings, AppLogger logger)
    {
        _settings = settings;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        var diagnostic = await RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        if (!diagnostic.TestRequestSucceeded)
        {
            throw CreateUserException(diagnostic);
        }
    }

    public async Task<GeminiDiagnosticResult> RunDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        var model = GetModelName();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new GeminiDiagnosticResult
            {
                ModelName = model,
                ErrorMessage = "API Key Gemini chưa được cấu hình."
            };
        }

        try
        {
            var models = await GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
            var modelInfo = models.FirstOrDefault(item => IsModelMatch(item.Name, model));

            if (modelInfo is null)
            {
                _logger.Info($"[Gemini API] Không tìm thấy model {model}. Models khả dụng: {string.Join(", ", models.Select(item => item.Name))}");
                return new GeminiDiagnosticResult
                {
                    ApiReachable = true,
                    AuthenticationSucceeded = true,
                    ModelFound = false,
                    ModelName = model,
                    ErrorMessage = $"Không tìm thấy model {model} trong danh sách model khả dụng."
                };
            }

            var generateContentSupported = modelInfo.SupportedGenerationMethods
                .Any(method => string.Equals(method, "generateContent", StringComparison.OrdinalIgnoreCase));
            if (!generateContentSupported)
            {
                _logger.Info($"[Gemini API] Model {model} tồn tại nhưng supportedMethods={string.Join(", ", modelInfo.SupportedGenerationMethods)}");
                return new GeminiDiagnosticResult
                {
                    ApiReachable = true,
                    AuthenticationSucceeded = true,
                    ModelFound = true,
                    GenerateContentSupported = false,
                    ModelName = model,
                    ErrorMessage = $"Model {model} không hỗ trợ generateContent với API/version hiện tại."
                };
            }

            return await TestGenerateModelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            LogNetworkException(ex);
            return new GeminiDiagnosticResult
            {
                ModelName = model,
                ErrorMessage = "Không thể kết nối tới máy chủ Gemini. Vui lòng kiểm tra Internet, Proxy hoặc Firewall."
            };
        }
        catch (TaskCanceledException ex)
        {
            _logger.Error("[Gemini API] Timeout khi kiểm tra kết nối Gemini.", ex);
            return new GeminiDiagnosticResult
            {
                ModelName = model,
                ErrorMessage = "Kiểm tra kết nối Gemini bị timeout."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    public async Task<IReadOnlyList<GeminiModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendGeminiRequestAsync(HttpMethod.Get, "models", requestBody: null, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        LogHttpResponse("List Models", BuildEndpoint("models"), response, body);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpException(response, body, "List Models");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("models", out var modelsElement)
            || modelsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GeminiModelInfo>();
        }

        var models = new List<GeminiModelInfo>();
        foreach (var modelElement in modelsElement.EnumerateArray())
        {
            var methods = modelElement.TryGetProperty("supportedGenerationMethods", out var methodsElement)
                && methodsElement.ValueKind == JsonValueKind.Array
                ? methodsElement.EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .ToArray()
                : Array.Empty<string>();

            models.Add(new GeminiModelInfo
            {
                Name = modelElement.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                DisplayName = modelElement.TryGetProperty("displayName", out var displayName) ? displayName.GetString() ?? string.Empty : string.Empty,
                SupportedGenerationMethods = methods
            });
        }

        return models;
    }

    public async Task<GeminiStructuredResponse> ProcessAudioAsync(
        byte[] audioData,
        CancellationToken cancellationToken)
    {
        EnsureApiKey();

        var wavAudio = AudioAnalysisHelper.WrapPcm16MonoAsWave(audioData, 16000);
        var modelEndpoint = $"models/{Uri.EscapeDataString(GetModelName())}:generateContent";
        var request = new
        {
            systemInstruction = new
            {
                parts = new[]
                {
                    new { text = BuildSystemInstruction() }
                }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new
                        {
                            inlineData = new
                            {
                                mimeType = "audio/wav",
                                data = Convert.ToBase64String(wavAudio)
                            }
                        },
                        new { text = "Process this meeting utterance and return only JSON that matches the required schema." }
                    }
                }
            },
            generationConfig = new
            {
                temperature = _settings.Temperature,
                responseMimeType = "application/json",
                responseSchema = CreateResponseSchema()
            }
        };

        using var response = await PostJsonAsync(modelEndpoint, request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        LogHttpResponse("Audio generateContent", BuildEndpoint(modelEndpoint), response, body);
        EnsureSuccess(response, body, "Gemini 3.6 Flash");

        var text = ExtractText(body);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Gemini không trả về JSON hợp lệ.");
        }

        var parsed = JsonSerializer.Deserialize<GeminiStructuredResponse>(text, _jsonOptions)
            ?? throw new InvalidOperationException("Gemini trả về JSON rỗng.");
        ValidateStructuredResponse(parsed);
        return parsed;
    }

    public async Task<byte[]> SynthesizeAsync(
        string text,
        SupportedLanguage language,
        CancellationToken cancellationToken)
    {
        EnsureApiKey();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Không có nội dung để tạo giọng nói Gemini.");
        }

        var request = new
        {
            model = _settings.TtsModel.Trim(),
            input = BuildTtsInput(text, language),
            response_format = new { type = "audio" },
            generation_config = new
            {
                speech_config = new[]
                {
                    new { voice = _settings.TtsVoice.Trim() }
                }
            }
        };

        using var response = await PostJsonAsync("interactions", request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        LogHttpResponse("Gemini TTS", BuildEndpoint("interactions"), response, body);
        EnsureSuccess(response, body, "Gemini TTS");

        var audioBytes = ExtractOutputAudio(body);
        if (audioBytes.Length == 0)
        {
            throw new InvalidOperationException("Gemini TTS không trả về âm thanh.");
        }

        return AudioAnalysisHelper.WrapPcm16MonoAsWave(audioBytes, 24000);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<GeminiDiagnosticResult> TestGenerateModelAsync(CancellationToken cancellationToken)
    {
        var model = GetModelName();
        var relativeUrl = $"models/{Uri.EscapeDataString(model)}:generateContent";
        var request = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = "Trả lời đúng một từ: OK" } }
                }
            },
            generationConfig = new
            {
                temperature = 0
            }
        };

        using var response = await PostJsonAsync(relativeUrl, request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        LogHttpResponse("Text generateContent", BuildEndpoint(relativeUrl), response, body);

        if (!response.IsSuccessStatusCode)
        {
            var error = ExtractGeminiError(body);
            return new GeminiDiagnosticResult
            {
                ApiReachable = true,
                AuthenticationSucceeded = response.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden,
                ModelFound = true,
                GenerateContentSupported = true,
                TestRequestSucceeded = false,
                HttpStatusCode = (int)response.StatusCode,
                ErrorCode = error.Code,
                ErrorMessage = error.Message,
                ModelName = model
            };
        }

        return new GeminiDiagnosticResult
        {
            ApiReachable = true,
            AuthenticationSucceeded = true,
            ModelFound = true,
            GenerateContentSupported = true,
            TestRequestSucceeded = true,
            HttpStatusCode = (int)response.StatusCode,
            ModelName = model
        };
    }

    private async Task<HttpResponseMessage> PostJsonAsync(
        string relativeUrl,
        object request,
        CancellationToken cancellationToken)
        => await SendGeminiRequestAsync(HttpMethod.Post, relativeUrl, request, cancellationToken).ConfigureAwait(false);

    private async Task<HttpResponseMessage> SendGeminiRequestAsync(
        HttpMethod method,
        string relativeUrl,
        object? requestBody,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(method, BuildEndpoint(relativeUrl));
        httpRequest.Headers.Add("x-goog-api-key", GetApiKey());
        if (requestBody is not null)
        {
            httpRequest.Content = JsonContent.Create(requestBody, options: _jsonOptions);
        }

        return await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsModelMatch(string availableModelName, string expectedModelName)
    {
        var normalizedAvailable = availableModelName.Trim();
        var normalizedExpected = expectedModelName.Trim();
        if (string.Equals(normalizedAvailable, normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(normalizedAvailable, $"models/{normalizedExpected}", StringComparison.OrdinalIgnoreCase);
    }

    private string GetApiKey() => _settings.ApiKey.Trim();

    private string GetModelName()
    {
        var model = string.IsNullOrWhiteSpace(_settings.Model)
            ? "gemini-3.6-flash"
            : _settings.Model.Trim();

        return model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? model["models/".Length..]
            : model;
    }

    private string GetBaseUrl()
    {
        var baseUrl = string.IsNullOrWhiteSpace(_settings.ApiBaseUrl)
            ? "https://generativelanguage.googleapis.com/v1beta"
            : _settings.ApiBaseUrl.Trim();

        return baseUrl.TrimEnd('/');
    }

    private string BuildEndpoint(string relativeUrl) => $"{GetBaseUrl()}/{relativeUrl.TrimStart('/')}";

    private Exception CreateUserException(GeminiDiagnosticResult diagnostic)
    {
        if (!string.IsNullOrWhiteSpace(diagnostic.ErrorMessage))
        {
            return new InvalidOperationException(diagnostic.ErrorMessage);
        }

        if (!diagnostic.AuthenticationSucceeded)
        {
            return new InvalidOperationException("API Key Gemini không hợp lệ hoặc chưa được cấp quyền.");
        }

        if (!diagnostic.ModelFound)
        {
            return new InvalidOperationException($"Không tìm thấy model {diagnostic.ModelName ?? GetModelName()} trong danh sách model khả dụng.");
        }

        if (!diagnostic.GenerateContentSupported)
        {
            return new InvalidOperationException($"Model {diagnostic.ModelName ?? GetModelName()} không hỗ trợ generateContent.");
        }

        return new InvalidOperationException("Không thể kiểm tra kết nối Gemini 3.6 Flash.");
    }

    private Exception CreateHttpException(HttpResponseMessage response, string body, string operation)
    {
        var error = ExtractGeminiError(body);
        var detail = string.IsNullOrWhiteSpace(error.Message) ? response.ReasonPhrase ?? string.Empty : error.Message;
        var status = (int)response.StatusCode;
        _logger.Error($"[Gemini API] {operation} lỗi HTTP {status}: {detail}");

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => new InvalidOperationException($"API Key Gemini không hợp lệ hoặc chưa được cấp quyền. HTTP {status}. {detail}"),
            HttpStatusCode.NotFound
                => new InvalidOperationException($"Không tìm thấy endpoint/model Gemini. HTTP {status}. {detail}"),
            HttpStatusCode.TooManyRequests
                => new InvalidOperationException($"Gemini đang giới hạn tần suất hoặc quota. HTTP {status}. {detail}"),
            HttpStatusCode.BadRequest
                => new InvalidOperationException($"Yêu cầu gửi tới Gemini chưa hợp lệ. HTTP {status}. {detail}"),
            _ => new InvalidOperationException($"Gemini trả lỗi HTTP {status}. {detail}")
        };
    }

    private (string? Code, string Message) ExtractGeminiError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString()
                    : error.TryGetProperty("code", out var codeElement)
                        ? codeElement.ToString()
                        : null;
                var message = error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString() ?? string.Empty
                    : string.Empty;
                return (code, message);
            }
        }
        catch (JsonException)
        {
        }

        return (null, body.Length > 0 ? TrimForLog(body) : string.Empty);
    }

    private void LogHttpResponse(string operation, string endpoint, HttpResponseMessage response, string body)
    {
        var message = new StringBuilder()
            .Append("[Gemini API] Operation=").Append(operation)
            .Append("; Model=").Append(GetModelName())
            .Append("; Endpoint=").Append(endpoint)
            .Append("; HTTP=").Append((int)response.StatusCode).Append(' ').Append(response.ReasonPhrase)
            .Append("; Response=").Append(TrimForLog(body))
            .ToString();

        if (response.IsSuccessStatusCode)
        {
            _logger.Info(message);
        }
        else
        {
            _logger.Error(message);
        }
    }

    private void LogNetworkException(Exception exception)
    {
        var inner = exception.InnerException is null
            ? string.Empty
            : $" Inner={exception.InnerException.GetType().Name}: {exception.InnerException.Message}";
        _logger.Error($"[Gemini API] Network error {exception.GetType().Name}: {exception.Message}.{inner}", exception);
    }

    private static string TrimForLog(string value)
    {
        const int maxLength = 4000;
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private static object CreateResponseSchema()
        => new
        {
            type = "object",
            properties = new
            {
                success = new { type = "boolean" },
                detectedLanguage = new { type = "string", @enum = new[] { "vi", "ko", "unknown" } },
                originalText = new { type = "string" },
                targetLanguage = new { type = "string", @enum = new[] { "vi", "ko", "unknown" } },
                translatedText = new { type = "string" },
                reason = new { type = "string" }
            },
            required = new[] { "success", "detectedLanguage", "originalText", "targetLanguage", "translatedText" }
        };

    private static string ExtractText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var parts = document.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts");

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text))
            {
                return text.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static byte[] ExtractOutputAudio(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (!TryFindStringProperty(document.RootElement, "data", out var data) || string.IsNullOrWhiteSpace(data))
        {
            return Array.Empty<byte>();
        }

        return Convert.FromBase64String(data);
    }

    private static bool TryFindStringProperty(JsonElement element, string propertyName, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString();
                    return true;
                }

                if (TryFindStringProperty(property.Value, propertyName, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindStringProperty(item, propertyName, out value))
                {
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    private static void ValidateStructuredResponse(GeminiStructuredResponse response)
    {
        if (!response.Success)
        {
            return;
        }

        var source = LanguageHelper.ParseLanguage(response.DetectedLanguage);
        var target = LanguageHelper.ParseLanguage(response.TargetLanguage);
        if (source == SupportedLanguage.Unknown || target == SupportedLanguage.Unknown)
        {
            throw new InvalidOperationException("Gemini trả về ngôn ngữ không hợp lệ.");
        }

        if (LanguageHelper.GetTargetLanguage(source) != target)
        {
            throw new InvalidOperationException("Gemini trả về hướng dịch không hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(response.OriginalText) || string.IsNullOrWhiteSpace(response.TranslatedText))
        {
            throw new InvalidOperationException("Gemini trả về nội dung rỗng.");
        }
    }

    private static string BuildTtsInput(string text, SupportedLanguage language)
    {
        var languageName = language == SupportedLanguage.Korean ? "Korean" : "Vietnamese";
        return $"Synthesize the following {languageName} business meeting translation exactly. Do not read instructions aloud. Transcript: {text}";
    }

    private void EnsureApiKey()
    {
        if (string.IsNullOrWhiteSpace(GetApiKey()))
        {
            throw new InvalidOperationException("Chưa cấu hình API Key Gemini.");
        }
    }

    private void EnsureSuccess(HttpResponseMessage response, string body, string serviceName)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = ExtractGeminiError(body);
        var message = string.IsNullOrWhiteSpace(error.Message) ? response.ReasonPhrase ?? string.Empty : error.Message;
        _logger.Error($"{serviceName} trả lỗi HTTP {(int)response.StatusCode}: {message}");

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => new InvalidOperationException($"Không thể sử dụng {serviceName} với API Key hiện tại. {message}"),
            HttpStatusCode.TooManyRequests
                => new InvalidOperationException($"Đã vượt giới hạn sử dụng {serviceName}. {message}"),
            HttpStatusCode.NotFound
                => new InvalidOperationException($"Model {serviceName} không khả dụng với API Key hiện tại. {message}"),
            HttpStatusCode.BadRequest
                => new InvalidOperationException($"{serviceName} không chấp nhận yêu cầu hiện tại. {message}"),
            _ => new InvalidOperationException($"Không thể kết nối đến {serviceName}. {message}")
        };
    }

    private static string BuildSystemInstruction()
        => """
You are a meeting interpreter for Vietnamese employees and Korean managers in a manufacturing company.

The input audio uses exactly one of these languages:
- Vietnamese
- Korean

Tasks:
1. Detect the spoken language accurately.
2. Transcribe the audio verbatim.
3. Do not add words the speaker did not say.
4. Do not summarize.
5. Do not explain.
6. If Vietnamese, translate naturally and accurately into Korean for a business/manufacturing meeting.
7. If Korean, translate naturally and accurately into Vietnamese for a business/manufacturing meeting.
8. Preserve proper names, company names, quantities, dates, product codes, PO, LOT, model, ERP, MES, SCM, PU01, and manufacturing terms.
9. Do not guess if the audio is unclear.
10. Return only JSON matching the schema.

Domain context, but do not force these terms into the transcript unless they were spoken:
Samsung, Changdae, Changdea, ERP, MES, SCM, NaverWorks, PU01, TPM, Production, Purchasing, Warehouse, Inventory, Stock, Material, Shipment, Delivery, Quality, sản lượng, kế hoạch sản xuất, kết quả sản xuất, tồn kho, nguyên vật liệu, vật tư, nhập kho, xuất kho, mua hàng, chất lượng, tiến độ, mã hàng, model, LOT, PO.

If audio is unclear, return:
{"success":false,"detectedLanguage":"unknown","originalText":"","targetLanguage":"unknown","translatedText":"","reason":"audio_unclear"}
""";
}
