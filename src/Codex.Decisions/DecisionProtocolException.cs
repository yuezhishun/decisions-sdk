namespace Codex.Decisions;

public sealed class DecisionProtocolException(int statusCode, string code, string message, string? parameter = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string? Parameter { get; } = parameter;
}
