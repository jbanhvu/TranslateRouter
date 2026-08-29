namespace MeetingInterpreter.Models;

public sealed class DeepgramSettings
{
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "nova-2";

    public string Language { get; set; } = "multi";

    public int EndpointingMs { get; set; } = 1000;

    public bool Punctuate { get; set; } = true;

    public bool InterimResults { get; set; } = true;

    public bool SmartFormat { get; set; } = true;
}
