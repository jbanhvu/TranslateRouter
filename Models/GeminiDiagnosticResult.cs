namespace MeetingInterpreter.Models;

public sealed class GeminiDiagnosticResult
{
    public bool ApiReachable { get; init; }

    public bool AuthenticationSucceeded { get; init; }

    public bool ModelFound { get; init; }

    public bool GenerateContentSupported { get; init; }

    public bool TestRequestSucceeded { get; init; }

    public int? HttpStatusCode { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ModelName { get; init; }
}
