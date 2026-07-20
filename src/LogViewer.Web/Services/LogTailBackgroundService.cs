using LogViewer.Web.Hubs;
using LogViewer.Web.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace LogViewer.Web.Services;

/// <summary>
/// Polls each registered application's log file(s) for today on a fixed
/// interval and pushes any new lines to connected browsers via SignalR.
///
/// Polling (rather than FileSystemWatcher) was chosen deliberately: it's
/// simpler to reason about, tolerates locked/rotating files without missed
/// events, and at the pilot's scale (a handful of apps, one server) a
/// couple of seconds of latency is well within the PRD's "near real-time"
/// requirement.
/// </summary>
public class LogTailBackgroundService : BackgroundService
{
    private readonly LogAppRegistry _registry;
    private readonly LogFolderScanner _scanner;
    private readonly IHubContext<LogHub> _hub;
    private readonly LogViewerOptions _options;
    private readonly ILogger<LogTailBackgroundService> _logger;

    private readonly Dictionary<string, FileTailState> _fileState = new();

    public LogTailBackgroundService(
        LogAppRegistry registry,
        LogFolderScanner scanner,
        IHubContext<LogHub> hub,
        IOptions<LogViewerOptions> options,
        ILogger<LogTailBackgroundService> logger)
    {
        _registry = registry;
        _scanner = scanner;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var app in _registry.All)
            {
                try
                {
                    await PollAppAsync(app, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error polling logs for {App}", app.Name);
                }
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task PollAppAsync(LogApplicationConfig app, CancellationToken ct)
    {
        var files = _scanner.GetFilesForToday(app);
        foreach (var file in files)
        {
            await TailFileAsync(app.Name, file, ct);
        }
    }

    private async Task TailFileAsync(string appName, string filePath, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(filePath);
            if (!info.Exists) return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stat {File}", filePath);
            return;
        }

        if (!_fileState.TryGetValue(filePath, out var state))
        {
            // First time this file is seen: start from the current end so
            // live tail only shows new activity, not the whole file's history.
            state = new FileTailState { Offset = info.Length };
            _fileState[filePath] = state;
            return;
        }

        var group = _hub.Clients.Group(LogHub.GroupName(appName));
        var fileName = Path.GetFileName(filePath);

        if (info.Length < state.Offset)
        {
            // File was rotated/truncated since the last poll. Whatever was
            // pending belonged to the content that just disappeared - flush it
            // now rather than waiting for a continuation line that will never
            // arrive, then restart from the top of the new file.
            if (state.Pending is not null)
            {
                await group.SendAsync("logLine", Redact(state.Pending), ct);
                state.Pending = null;
            }
            state.Offset = 0;
        }

        if (info.Length > state.Offset)
        {
            string text;
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(state.Offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                text = await reader.ReadToEndAsync(ct);
            }
            catch (IOException)
            {
                // File briefly locked by the writer or mid-rotation - try again next tick.
                return;
            }

            state.Offset = info.Length;

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0);

            foreach (var line in lines)
            {
                if (LogLineParser.HasTimestamp(line)) state.UsesTimestamps = true;

                // With no timestamp anywhere in this file yet, every line is its
                // own entry (same fallback as the batch/History grouper) - once
                // timestamps are established, a line without one is a
                // continuation (stack trace, wrapped message) of the entry
                // currently being built up.
                var isNewEntry = !state.UsesTimestamps || LogLineParser.HasTimestamp(line);

                if (isNewEntry || state.Pending is null)
                {
                    if (state.Pending is not null) await group.SendAsync("logLine", Redact(state.Pending), ct);
                    state.Pending = LogLineParser.Parse(line, fileName);
                }
                else
                {
                    state.Pending!.Message += Environment.NewLine + line;
                }

                state.PendingSince = DateTime.UtcNow;
            }
        }

        // Don't hold a multi-line entry forever waiting for a continuation line
        // that never comes - flush it once it's been sitting for a couple of
        // poll intervals, so live tail doesn't silently swallow the last entry
        // written before an app goes quiet.
        var flushTimeout = TimeSpan.FromSeconds(Math.Max(4, _options.PollIntervalSeconds * 2));
        if (state.Pending is not null && DateTime.UtcNow - state.PendingSince > flushTimeout)
        {
            await group.SendAsync("logLine", Redact(state.Pending), ct);
            state.Pending = null;
        }
    }

    /// <summary>
    /// Masked at the point of sending rather than at parse time: continuation
    /// lines are appended to a pending entry after it is parsed, so redacting
    /// earlier would miss secrets inside stack traces.
    /// </summary>
    private LogEntry Redact(LogEntry entry) =>
        _options.RedactSecrets ? LogRedactor.Apply(entry) : entry;

    private class FileTailState
    {
        public long Offset;
        public bool UsesTimestamps;
        public LogEntry? Pending;
        public DateTime PendingSince;
    }
}
