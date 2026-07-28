namespace Contracts.Engineering;

public sealed class EngineeringProgress
{
    public EngineeringProgress(string message) => Message = message;

    public string Message { get; }
}
