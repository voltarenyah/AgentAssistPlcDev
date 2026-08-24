namespace PlcXml.Model;

public sealed class PlcNetwork
{
    internal PlcNetwork(PlcDocument document, PlcObject source, int occurrence)
    {
        Document = document;
        Source = source;
        Occurrence = occurrence;
    }

    private PlcDocument Document { get; }
    internal PlcObject Source { get; }
    internal int Occurrence { get; }
    public string? Id => Source.Id;
    public PlcLocation Location => Source.Location;

    public void SetTitleText(string culture, string text) =>
        Document.QueueMutation(this, "Title", culture, text);

    public void SetCommentText(string culture, string text) =>
        Document.QueueMutation(this, "Comment", culture, text);
}
