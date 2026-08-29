namespace MeetingInterpreter.Models;

public sealed class PipelineMetrics
{
    public long TotalSttFinal;

    public long TotalTranslationQueued;

    public long TotalTranslated;

    public long TotalTtsCompleted;

    public long TotalPlaybackCompleted;

    public long TotalFailed;

    public long TotalReconnects;
}
