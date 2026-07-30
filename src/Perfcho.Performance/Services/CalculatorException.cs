namespace Perfcho.Performance.Services;

public sealed class CalculatorException(int statusCode, string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
