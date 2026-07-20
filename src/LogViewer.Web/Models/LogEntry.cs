namespace LogViewer.Web.Models;

public enum LogSeverity
{
    Unknown,
    Debug,
    Info,
    Warn,
    Error
}

public class LogEntry
{
    public DateTime? Timestamp { get; set; }
    public LogSeverity Level { get; set; }
    public string SourceFile { get; set; } = "";
    public string Message { get; set; } = "";

    /// <summary>
    /// Structured fields discovered in the line - key=value pairs, bracketed
    /// tokens, or JSON properties. Populated best-effort: a log format that
    /// carries none of these simply yields an empty dictionary, it is never
    /// an error. These are what the History page's "group by" offers.
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
