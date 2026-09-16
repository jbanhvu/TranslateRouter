namespace MeetingInterpreter.Models;

public enum UtteranceStage
{
    Captured,
    Recognizing,
    SttFinal,
    Merged,
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
