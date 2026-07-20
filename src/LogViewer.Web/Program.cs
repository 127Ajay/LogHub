using System.Text.Json.Serialization;
using LogViewer.Web.Hubs;
using LogViewer.Web.Models;
using LogViewer.Web.Services;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// LogHub's own diagnostics go through Serilog to a rolling daily file, separate
// from the *.log files it's reading for the monitored applications. This is the
// app's own operational log (startup, scan/tail errors, HTTP requests) - not
// something the Live Tail / History pages read back, at least not yet.
builder.Host.UseSerilog((context, services, loggerConfig) => loggerConfig
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(context.HostingEnvironment.ContentRootPath, "Logs", "loghub-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        // Keep timestamp + level + message on one physical line - LogHub's own
        // scanner/tailer is line-based (one log entry = one line), so splitting
        // the message onto its own line (as Serilog's default template does)
        // would break its own Live Tail / History views on its own log file.
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"));

builder.Services.Configure<LogViewerOptions>(builder.Configuration.GetSection("LogViewer"));
builder.Services.AddSingleton<LogAppRegistry>();
builder.Services.AddSingleton<LogFolderScanner>();
builder.Services.AddHostedService<LogTailBackgroundService>();

// Serialize enums (log level) as strings, both over SignalR and the JSON API,
// so the browser sees "Error"/"Warn" instead of raw integers.
builder.Services.AddSignalR().AddJsonProtocol(options =>
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseSerilogRequestLogging();

app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();
app.MapHub<LogHub>("/hubs/log");

// ---- Read-only JSON API backing the Live Tail / History pages ----
// No auth: this app is intended for the internal network only (see PRD v1.1, Section 12).

app.MapGet("/api/apps", (LogAppRegistry registry) =>
    Results.Ok(registry.All));

app.MapPost("/api/apps", async (HttpRequest request, LogAppRegistry registry, LogFolderScanner scanner) =>
{
    var payload = await request.ReadFromJsonAsync<AddAppRequest>();
    if (payload is null) return Results.BadRequest(new { ok = false, error = "Invalid request body." });

    var (success, error) = registry.Add(payload.Name, payload.RootPaths ?? new List<string>());
    // A newly registered app must show its logs straight away, not after the
    // index cache TTL expires.
    if (success) scanner.InvalidateCache();

    return success
        ? Results.Ok(new { ok = true })
        : Results.BadRequest(new { ok = false, error });
});

app.MapDelete("/api/apps/{name}", (string name, LogAppRegistry registry, LogFolderScanner scanner) =>
{
    if (!registry.Remove(name)) return Results.NotFound();
    scanner.InvalidateCache();
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/apps/{name}/dates", (string name, LogAppRegistry registry, LogFolderScanner scanner) =>
{
    var appConfig = registry.Get(name);
    if (appConfig is null) return Results.NotFound();

    var dates = scanner.GetDates(appConfig).Select(d => d.ToString("yyyy-MM-dd"));
    return Results.Ok(dates);
});

app.MapGet("/api/apps/{name}/files", (string name, string date, LogAppRegistry registry, LogFolderScanner scanner) =>
{
    var appConfig = registry.Get(name);
    if (appConfig is null) return Results.NotFound();
    if (!DateOnly.TryParse(date, out var parsedDate)) return Results.BadRequest("Invalid date");

    var files = scanner.GetFilesForDate(appConfig, parsedDate).Select(Path.GetFileName);
    return Results.Ok(files);
});

// Distinct tag keys (and their values) discovered in a day's logs. Drives the
// History page's "group by" dropdown: the options are whatever the log format
// actually turned out to contain, since LogHub is never told in advance which
// fields a given application writes.
app.MapGet("/api/apps/{name}/tags", (
    string name,
    string date,
    string? file,
    LogAppRegistry registry,
    LogFolderScanner scanner,
    IOptions<LogViewerOptions> options) =>
{
    var appConfig = registry.Get(name);
    if (appConfig is null) return Results.NotFound();
    if (!DateOnly.TryParse(date, out var parsedDate)) return Results.BadRequest("Invalid date");

    var files = FilterFiles(scanner.GetFilesForDate(appConfig, parsedDate), file);
    var tags = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    var scanned = 0;

    foreach (var filePath in files)
    {
        if (scanned >= options.Value.MaxHistoryLinesReturned) break;

        List<string> lines;
        try { lines = LogFileReader.ReadLinesShared(filePath).ToList(); }
        catch (IOException) { continue; }

        foreach (var entry in LogEntryGrouper.Group(lines, Path.GetFileName(filePath), options.Value.RedactSecrets))
        {
            if (++scanned >= options.Value.MaxHistoryLinesReturned) break;

            foreach (var (key, value) in entry.Tags)
            {
                // Never offer a credential-ish field as a grouping option -
                // the dropdown would list its values verbatim.
                if (options.Value.RedactSecrets && LogRedactor.IsSecretKey(key)) continue;
                if (!tags.TryGetValue(key, out var values))
                {
                    values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    tags[key] = values;
                }

                // A key with hundreds of distinct values (a request id, say) is
                // useless to group by - cap it so the dropdown stays usable.
                if (values.Count < 50) values.Add(value);
            }
        }
    }

    var result = tags
        .Where(kv => kv.Value.Count is > 0 and <= 50)
        .OrderBy(kv => kv.Key)
        .Select(kv => new { key = kv.Key, values = kv.Value.OrderBy(v => v).ToList() });

    return Results.Ok(result);
});

app.MapGet("/api/apps/{name}/logs", (
    string name,
    string date,
    string? level,
    string? keyword,
    string? file,
    string? tagKey,
    string? tagValue,
    bool? regex,
    LogAppRegistry registry,
    LogFolderScanner scanner,
    IOptions<LogViewerOptions> options) =>
{
    var appConfig = registry.Get(name);
    if (appConfig is null) return Results.NotFound();
    if (!DateOnly.TryParse(date, out var parsedDate)) return Results.BadRequest("Invalid date");

    var query = LogQuery.Parse(keyword, regex == true);
    if (!query.IsValid) return Results.BadRequest(new { error = query.Error });

    var files = FilterFiles(scanner.GetFilesForDate(appConfig, parsedDate), file);
    var results = ReadEntries(files, options.Value, query, level, tagKey, tagValue,
        options.Value.MaxHistoryLinesReturned);

    return Results.Ok(results);
});

// Export a whole date range in one request, so a multi-day extract doesn't
// require paging the UI a day at a time. Streams CSV rather than JSON: the
// output is meant for Excel, and a large range shouldn't be buffered whole.
app.MapGet("/api/apps/{name}/export", (
    string name,
    string from,
    string to,
    string? level,
    string? keyword,
    string? tagKey,
    string? tagValue,
    bool? regex,
    LogAppRegistry registry,
    LogFolderScanner scanner,
    IOptions<LogViewerOptions> options) =>
{
    var appConfig = registry.Get(name);
    if (appConfig is null) return Results.NotFound();
    if (!DateOnly.TryParse(from, out var fromDate)) return Results.BadRequest("Invalid 'from' date");
    if (!DateOnly.TryParse(to, out var toDate)) return Results.BadRequest("Invalid 'to' date");
    if (toDate < fromDate) (fromDate, toDate) = (toDate, fromDate);

    var query = LogQuery.Parse(keyword, regex == true);
    if (!query.IsValid) return Results.BadRequest(new { error = query.Error });

    // Only dates that actually have files, so an open-ended range doesn't
    // walk empty days one by one.
    var dates = scanner.GetDates(appConfig)
        .Where(d => d >= fromDate && d <= toDate)
        .OrderBy(d => d)
        .ToList();

    var fileName = $"{appConfig.Name}_{fromDate:yyyy-MM-dd}_to_{toDate:yyyy-MM-dd}.csv";

    return Results.Stream(async stream =>
    {
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync("Date,Timestamp,Level,File,Message,Tags");

        var budget = options.Value.MaxExportEntries;

        foreach (var date in dates)
        {
            if (budget <= 0) break;

            var files = scanner.GetFilesForDate(appConfig, date);
            var entries = ReadEntries(files, options.Value, query, level, tagKey, tagValue, budget);
            budget -= entries.Count;

            foreach (var entry in entries)
            {
                var tags = string.Join(" ", entry.Tags.Select(t => $"{t.Key}={t.Value}"));
                await writer.WriteLineAsync(string.Join(",",
                    Csv(date.ToString("yyyy-MM-dd")),
                    Csv(entry.Timestamp?.ToString("o") ?? ""),
                    Csv(entry.Level.ToString()),
                    Csv(entry.SourceFile),
                    Csv(entry.Message),
                    Csv(tags)));
            }
        }
    }, "text/csv", fileName);
});

try
{
    app.Run();
}
finally
{
    // Flush any buffered log events before the process exits.
    Log.CloseAndFlush();
}

record AddAppRequest(string Name, List<string>? RootPaths);

public partial class Program
{
    /// <summary>Narrows a date's files to a single one when the caller asked for it.</summary>
    internal static List<string> FilterFiles(List<string> files, string? fileName) =>
        string.IsNullOrEmpty(fileName)
            ? files
            : files.Where(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Reads and filters entries from a set of files. Shared by the History
    /// search and the date-range export so the two can never drift into
    /// applying filters differently.
    /// </summary>
    internal static List<LogEntry> ReadEntries(
        List<string> files,
        LogViewerOptions options,
        LogQuery query,
        string? level,
        string? tagKey,
        string? tagValue,
        int maxEntries)
    {
        LogSeverity? levelFilter = null;
        if (!string.IsNullOrEmpty(level) && Enum.TryParse<LogSeverity>(level, true, out var parsedLevel))
        {
            levelFilter = parsedLevel;
        }

        var results = new List<LogEntry>();

        foreach (var filePath in files)
        {
            if (results.Count >= maxEntries) break;

            List<string> lines;
            try
            {
                // Materialized here so an IOException surfaces now rather than
                // part-way through enumeration inside the grouper.
                lines = LogFileReader.ReadLinesShared(filePath).ToList();
            }
            catch (IOException)
            {
                continue; // genuinely unreadable - skip for this request
            }

            var fileName = Path.GetFileName(filePath);
            foreach (var entry in LogEntryGrouper.Group(lines, fileName, options.RedactSecrets))
            {
                // Matched against the grouped entry, so a term inside a stack
                // trace still finds the entry that owns it.
                if (!query.Matches(entry)) continue;
                if (levelFilter.HasValue && entry.Level != levelFilter.Value) continue;
                if (!string.IsNullOrEmpty(tagKey))
                {
                    if (!entry.Tags.TryGetValue(tagKey, out var actual)) continue;
                    if (!string.IsNullOrEmpty(tagValue) &&
                        !string.Equals(actual, tagValue, StringComparison.OrdinalIgnoreCase)) continue;
                }

                results.Add(entry);
                if (results.Count >= maxEntries) break;
            }
        }

        return results;
    }

    /// <summary>RFC 4180 CSV field: always quoted, embedded quotes doubled.</summary>
    internal static string Csv(string value) => '"' + (value ?? "").Replace("\"", "\"\"") + '"';
}
