namespace MeetingInterpreter.Models;

public sealed class GeminiModelInfo
{
    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public IReadOnlyList<string> SupportedGenerationMethods { get; init; } = Array.Empty<string>();
}
