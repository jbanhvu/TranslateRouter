namespace MeetingInterpreter.Models;

public sealed class PipelineRuntimeState
{
    public bool IsListening { get; set; }

    public bool IsSttConnected { get; set; }

    public bool IsReconnectingStt { get; set; }

    public bool IsTranslating { get; set; }

    public bool IsSynthesizing { get; set; }

    public bool IsPlaying { get; set; }

    public int PendingTranslations { get; set; }

    public int PendingPlaybackItems { get; set; }
}
