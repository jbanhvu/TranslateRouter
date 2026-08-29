namespace MeetingInterpreter.Models;

public sealed class StreamingTranscriptEventArgs : EventArgs
{
    public StreamingTranscriptEventArgs(string text, SupportedLanguage language, float? confidence, string rawLanguageCode = "")
    {
        Text = text;
        Language = language;
        Confidence = confidence;
        RawLanguageCode = rawLanguageCode;
    }

    public string Text { get; }

    public SupportedLanguage Language { get; }

    public float? Confidence { get; }

    public string RawLanguageCode { get; }
}
