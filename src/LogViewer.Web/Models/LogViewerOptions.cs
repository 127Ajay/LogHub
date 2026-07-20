namespace LogViewer.Web.Models;

public class LogApplicationConfig
{
    public string Name { get; set; } = "";
    public List<string> RootPaths { get; set; } = new();
}

public class LogViewerOptions
{
    public int PollIntervalSeconds { get; set; } = 2;
    public int MaxHistoryLinesReturned { get; set; } = 5000;

    /// <summary>
    /// How many existing entries from today's file(s) to replay to a client
    /// when it opens Live Tail, so the view isn't blank until the monitored
    /// application happens to write its next line. Set to 0 to disable.
    /// </summary>
    public int LiveTailBacklogLines { get; set; } = 200;

    /// <summary>
    /// How long a built date -> files index stays usable before it is rebuilt.
    /// The folder scan is recursive, and it previously ran on every History
    /// request *and* every tail poll tick; caching it is the "in-memory
    /// indexing" of the Phase 2 plan. Deliberately in-memory and short-lived
    /// rather than persisted - a new log file shows up within this many
    /// seconds. Set to 0 to disable caching entirely.
    /// </summary>
    public int IndexCacheSeconds { get; set; } = 10;

    /// <summary>
    /// Mask values that look like secrets (passwords, tokens, API keys) before
    /// they leave the server. A safety net only - the expectation remains that
    /// source applications don't log secrets in the first place.
    /// </summary>
    public bool RedactSecrets { get; set; } = true;

    /// <summary>Upper bound on entries returned by a multi-day export.</summary>
    public int MaxExportEntries { get; set; } = 100_000;
}
