namespace MeetingInterpreter.Models;

public enum UtteranceStage
{
    SttFinal,
    QueuedForTranslation,
    Translating,
    Translated,
    QueuedForTts,
    Synthesizing,
    ReadyForPlayback,
    Playing,
    Completed,
    Failed
}
