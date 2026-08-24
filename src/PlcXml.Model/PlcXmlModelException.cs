namespace PlcXml.Model;

public class PlcXmlModelException : Exception
{
    public PlcXmlModelException(string code, string message, string? sourceName = null, Exception? innerException = null, PlcLocation? location = null)
        : base(message, innerException)
    {
        Code = code;
        SourceName = sourceName;
        Location = location;
    }

    public string Code { get; }
    public string? SourceName { get; }
    public PlcLocation? Location { get; }
}

public sealed class PlcXmlParseException : PlcXmlModelException
{
    public PlcXmlParseException(string code, string message, string? sourceName = null, Exception? innerException = null, PlcLocation? location = null)
        : base(code, message, sourceName, innerException, location) { }
}
