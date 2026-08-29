using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public sealed class GoogleCloudSpeechSynthesisService : ISpeechSynthesisService
{
    private readonly GoogleTranslatePipeline _pipeline;

    public GoogleCloudSpeechSynthesisService(GoogleTranslatePipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public Task<byte[]> SynthesizeAsync(
        string text,
        SupportedLanguage language,
        CancellationToken cancellationToken)
        => _pipeline.SynthesizeSpeechAsync(text, language, cancellationToken);
}
