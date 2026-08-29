using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public interface IInterpreterEngine
{
    InterpreterEngineType EngineType { get; }

    Task<InterpreterResult?> ProcessAudioAsync(
        byte[] audioData,
        CancellationToken cancellationToken);
}
