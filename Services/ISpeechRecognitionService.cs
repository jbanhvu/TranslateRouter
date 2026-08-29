using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public interface ISpeechRecognitionService
{
    Task<SpeechRecognitionResultModel?> RecognizeSpeechAsync(
        byte[] audioData,
        CancellationToken cancellationToken);
}
