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
}
