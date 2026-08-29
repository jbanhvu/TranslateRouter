using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public interface ISpeechSynthesisService
{
    Task<byte[]> SynthesizeAsync(
        string text,
        SupportedLanguage language,
        CancellationToken cancellationToken);
}
