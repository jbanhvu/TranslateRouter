namespace MeetingInterpreter.Models;

public enum InterpreterState
{
    Idle,
    Listening,
    SpeechDetected,
    ProcessingSpeech,
    Translating,
    Synthesizing,
    Playing,
    Error
}
