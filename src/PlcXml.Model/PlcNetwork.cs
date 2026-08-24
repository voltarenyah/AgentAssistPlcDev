namespace PlcXml.Model;

public sealed class PlcNetwork
{
    internal PlcNetwork(PlcDocument document, PlcObject source)
    {
        Document = document;
        Source = source;
    }

    private PlcDocument Document { get; }
    internal PlcObject Source { get; }
    public string? Id => Source.Id;
    public PlcLocation Location => Source.Location;

    public void SetTitleText(string culture, string text) =>
        Document.QueueMutation(this, "Title", culture, text);

    public void SetCommentText(string culture, string text) =>
        Document.QueueMutation(this, "Comment", culture, text);
}
