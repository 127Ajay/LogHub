using LogViewer.Web.Models;
using LogViewer.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace LogViewer.Web.Hubs;

/// <summary>
/// Browser clients join a group named after the application they're
/// tailing, so a new log line only gets pushed to viewers who are actually
/// watching that application.
///
/// Joining also replays the tail end of today's file(s) to the caller. The
/// background tailer only ever pushes lines written *after* it first saw a
/// file, so without this replay an application that isn't actively logging
/// leaves the Live Tail view blank indefinitely - which reads as "the feature
/// is broken" rather than "nothing has happened yet".
/// </summary>
public class LogHub : Hub
{
    private readonly LogAppRegistry _registry;
    private readonly LogFolderScanner _scanner;
    private readonly LogViewerOptions _options;
    private readonly ILogger<LogHub> _logger;

    public LogHub(
        LogAppRegistry registry,
        LogFolderScanner scanner,
        IOptions<LogViewerOptions> options,
        ILogger<LogHub> logger)
    {
        _registry = registry;
        _scanner = scanner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task JoinApp(string appName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(appName));
        await SendBacklogAsync(appName);
    }

    public Task LeaveApp(string appName) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(appName));

    public static string GroupName(string appName) => "app:" + appName;

    private async Task SendBacklogAsync(string appName)
    {
        var appConfig = _registry.Get(appName);
        if (appConfig is null) return;

        var backlogSize = Math.Max(0, _options.LiveTailBacklogLines);
        if (backlogSize == 0) return;

        var entries = new List<LogEntry>();

        foreach (var filePath in _scanner.GetFilesForToday(appConfig))
        {
            try
            {
                var fileName = Path.GetFileName(filePath);
                entries.AddRange(LogEntryGrouper.Group(
                    LogFileReader.ReadLinesShared(filePath), fileName, _options.RedactSecrets));
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not read backlog from {File}", filePath);
            }
        }

        var recent = entries
            .OrderBy(e => e.Timestamp ?? DateTime.MinValue)
            .TakeLast(backlogSize);

        foreach (var entry in recent)
        {
            await Clients.Caller.SendAsync("logLine", entry);
        }
    }
}
