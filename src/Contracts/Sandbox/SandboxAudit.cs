using System.Text.Json;

namespace Contracts.Sandbox;

/// <summary>
/// Append-only JSONL audit trail, one file per process (auditDirectory\&lt;source&gt;.jsonl):
/// every sandbox decision — allow/deny per tool call, confirmations, budget stops. Writes are
/// best-effort: an audit failure is recorded in <see cref="LastError"/> but never breaks a tool call.
/// </summary>
public sealed class SandboxAudit
{
    private const int DetailMaxChars = 240;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object gate = new();
    private readonly string source;

    public SandboxAudit(string directory, string source)
    {
        this.source = source;
        FilePath = Path.Combine(directory, source + ".jsonl");
    }

    public string FilePath { get; }

    /// <summary>Error of the last failed write, null when the trail is healthy.</summary>
    public string? LastError { get; private set; }

    public void Record(string tool, string tier, string decision, string? detail = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new AuditLine
            {
                Ts = DateTimeOffset.Now,
                Src = source,
                Tool = tool,
                Tier = tier,
                Decision = decision,
                Detail = Truncate(detail),
            }, Json);
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }

            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private static string? Truncate(string? detail) =>
        detail == null || detail.Length <= DetailMaxChars ? detail : detail.Substring(0, DetailMaxChars) + "…";

    private sealed class AuditLine
    {
        public DateTimeOffset Ts { get; set; }
        public string? Src { get; set; }
        public string? Tool { get; set; }
        public string? Tier { get; set; }
        public string? Decision { get; set; }
        public string? Detail { get; set; }
    }
}
