using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class InterpreterEngineFactory
{
    private readonly IReadOnlyDictionary<InterpreterEngineType, IInterpreterEngine> _engines;
    private readonly IReadOnlyDictionary<InterpreterEngineType, ISpeechSynthesisService> _speechSynthesisServices;

    public InterpreterEngineFactory(
        GeminiInterpreterEngine geminiEngine,
        GoogleCloudInterpreterEngine googleCloudEngine,
        GeminiApiClient geminiSpeechSynthesis,
        GoogleCloudSpeechSynthesisService googleCloudSpeechSynthesis)
    {
        _engines = new Dictionary<InterpreterEngineType, IInterpreterEngine>
        {
            [InterpreterEngineType.Gemini25Pro] = geminiEngine,
            [InterpreterEngineType.GoogleCloudPipeline] = googleCloudEngine,
            [InterpreterEngineType.GoogleCloudHybridPipeline] = googleCloudEngine,
            [InterpreterEngineType.GoogleCloudStreamingPipeline] = googleCloudEngine,
            [InterpreterEngineType.GoogleCloudAdvancedHybridPipeline] = googleCloudEngine,
            [InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline] = googleCloudEngine,
            [InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline] = googleCloudEngine
        };
        _speechSynthesisServices = new Dictionary<InterpreterEngineType, ISpeechSynthesisService>
        {
            [InterpreterEngineType.Gemini25Pro] = geminiSpeechSynthesis,
            [InterpreterEngineType.GoogleCloudPipeline] = googleCloudSpeechSynthesis,
            [InterpreterEngineType.GoogleCloudHybridPipeline] = googleCloudSpeechSynthesis,
            [InterpreterEngineType.GoogleCloudStreamingPipeline] = googleCloudSpeechSynthesis,
            [InterpreterEngineType.GoogleCloudAdvancedHybridPipeline] = googleCloudSpeechSynthesis,
            [InterpreterEngineType.GoogleCloudAdaptiveHybridPipeline] = googleCloudSpeechSynthesis,
            [InterpreterEngineType.GoogleCloudPhysicalMuteHybridPipeline] = googleCloudSpeechSynthesis
        };
    }

    public IInterpreterEngine GetEngine(InterpreterEngineType engineType)
        => _engines.TryGetValue(engineType, out var engine)
            ? engine
            : throw new NotSupportedException("Mô hình phiên dịch không được hỗ trợ.");

    public ISpeechSynthesisService GetSpeechSynthesisService(InterpreterEngineType engineType)
        => _speechSynthesisServices.TryGetValue(engineType, out var service)
            ? service
            : throw new NotSupportedException("Dịch vụ tạo giọng nói không được hỗ trợ.");
}
