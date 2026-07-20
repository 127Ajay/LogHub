using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogViewer.Web.Models;

namespace LogViewer.Web.Services;

/// <summary>
/// Best-effort parsing of a raw log line into a timestamp + severity + message
/// + any structured tags it happens to carry.
///
/// Nothing here is tied to one framework. A line that matches none of the
/// patterns is still a valid entry - it just comes back with a null timestamp,
/// Unknown severity and no tags. The app never rejects a line for not matching
/// a schema, because logs arrive from Serilog, NLog, log4net, python logging,
/// IIS, Docker, or plain Console.WriteLine, and LogHub is not told in advance
/// which one it is looking at.
/// </summary>
public static class LogLineParser
{
    // Optional leading noise before a timestamp: whitespace, and the brackets
    // or quotes that many layouts wrap the timestamp in - "[2026-07-20 08:00:00]",
    // "<2026-07-20T08:00:00Z>", '"2026-07-20 08:00:00"'. Without this the very
    // common bracketed layouts read as "no timestamp at all" and every line
    // collapses into a single entry.
    private const string LeadIn = @"^[\s\[\(<""']*";

    // Ordered most-specific first. Each alternative captures the timestamp text
    // in group 1 so Parse can hand it to the date parsers below.
    private static readonly Regex TimestampRegex = new(
        LeadIn + @"(" + string.Join("|", new[]
        {
            // 2026-07-20T08:00:00.123+05:30 / 2026-07-20 08:00:00,123 / ...Z
            @"\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:\s?Z|\s?[+-]\d{2}:?\d{2})?",
            // 2026/07/20 08:00:00.123
            @"\d{4}/\d{2}/\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?",
            // 20/07/2026 08:00:00 or 07-20-2026 08:00:00
            @"\d{2}[/-]\d{2}[/-]\d{4}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?",
            // 20260720 080000 / 20260720T080000
            @"\d{8}[ T]\d{6}(?:[.,]\d+)?",
            // syslog: Jul 20 08:00:00
            @"(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}",
            // bare time-only layouts: 08:00:00.123
            @"\d{2}:\d{2}:\d{2}(?:[.,]\d+)?"
        }) + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Serilog's default {Level:u3} output is a bracketed 3-letter code, e.g.
    // "[INF]" / "[WRN]" / "[ERR]" - this is how LogHub's own log looks, and
    // a common convention for other .NET apps too, so it's checked first.
    private static readonly Regex SerilogLevelRegex = new(
        @"\[\s*(VRB|TRC|DBG|INF|WRN|ERR|FTL|CRT)\s*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Full level words, but only where a level plausibly sits: inside brackets,
    // after a colon/pipe, or as a standalone token. Matching them anywhere in
    // the line makes any message containing the word "error" an Error entry.
    private static readonly Regex WordLevelRegex = new(
        @"(?<![A-Za-z])(FATAL|CRITICAL|ERROR|SEVERE|WARNING|WARN|NOTICE|INFORMATION|INFO|DEBUG|TRACE|VERBOSE)(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // key=value / key="value with spaces" / key='value'
    private static readonly Regex KeyValueRegex = new(
        @"(?<![\w.])(?<key>[A-Za-z_][\w.\-]{0,40})=(?<value>""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.Compiled);

    // Bracketed tokens that aren't the level and aren't the timestamp -
    // thread names, categories, correlation ids: "[AuthService]", "[thread-4]".
    private static readonly Regex BracketTokenRegex = new(
        @"\[([^\[\]]{1,60})\]",
        RegexOptions.Compiled);

    private static readonly HashSet<string> LevelWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "VRB", "TRC", "DBG", "INF", "WRN", "ERR", "FTL", "CRT",
        "FATAL", "CRITICAL", "ERROR", "SEVERE", "WARNING", "WARN",
        "NOTICE", "INFORMATION", "INFO", "DEBUG", "TRACE", "VERBOSE"
    };

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss",
        "yyyy/MM/dd HH:mm:ss.fff", "yyyy/MM/dd HH:mm:ss",
        "dd/MM/yyyy HH:mm:ss.fff", "dd/MM/yyyy HH:mm:ss",
        "MM/dd/yyyy HH:mm:ss.fff", "MM/dd/yyyy HH:mm:ss",
        "MM-dd-yyyy HH:mm:ss.fff", "MM-dd-yyyy HH:mm:ss",
        "yyyyMMdd HHmmss", "yyyyMMddTHHmmss",
        "MMM d HH:mm:ss", "MMM dd HH:mm:ss",
        "HH:mm:ss.fff", "HH:mm:ss"
    };

    /// <summary>
    /// True if this line starts with something that looks like a log timestamp.
    /// Used to decide entry boundaries: a line with a timestamp starts a new
    /// entry, a line without one is a continuation of the previous entry
    /// (stack trace, wrapped message, etc.) - see LogEntryGrouper.
    /// </summary>
    public static bool HasTimestamp(string line) => TimestampRegex.IsMatch(line);

    public static LogEntry Parse(string line, string sourceFile)
    {
        // A JSON-per-line log (Serilog compact, Bunyan, pino, Docker) carries
        // everything as real fields - use them directly rather than pattern
        // matching against the serialized text.
        if (TryParseJsonLine(line, sourceFile, out var jsonEntry)) return jsonEntry!;

        var entry = new LogEntry
        {
            Message = line,
            SourceFile = sourceFile,
            Level = DetectLevel(line),
            Tags = ExtractTags(line)
        };

        var match = TimestampRegex.Match(line);
        if (match.Success && TryParseTimestamp(match.Groups[1].Value, out var timestamp))
        {
            entry.Timestamp = timestamp;
        }

        return entry;
    }

    public static LogSeverity DetectLevel(string line)
    {
        var serilogMatch = SerilogLevelRegex.Match(line);
        if (serilogMatch.Success) return MapLevel(serilogMatch.Groups[1].Value);

        var wordMatch = WordLevelRegex.Match(line);
        return wordMatch.Success ? MapLevel(wordMatch.Groups[1].Value) : LogSeverity.Unknown;
    }

    private static LogSeverity MapLevel(string token) => token.ToUpperInvariant() switch
    {
        "FTL" or "CRT" or "ERR" or "FATAL" or "CRITICAL" or "ERROR" or "SEVERE" => LogSeverity.Error,
        "WRN" or "WARN" or "WARNING" => LogSeverity.Warn,
        "DBG" or "VRB" or "TRC" or "DEBUG" or "TRACE" or "VERBOSE" => LogSeverity.Debug,
        "INF" or "INFO" or "INFORMATION" or "NOTICE" => LogSeverity.Info,
        _ => LogSeverity.Unknown
    };

    /// <summary>
    /// Pulls whatever structured fields the line happens to expose. Formats
    /// that carry none return an empty dictionary - that is a normal outcome,
    /// not a parse failure.
    /// </summary>
    public static Dictionary<string, string> ExtractTags(string line)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in KeyValueRegex.Matches(line))
        {
            var key = m.Groups["key"].Value;
            var value = m.Groups["value"].Value.Trim('"', '\'');
            if (value.Length == 0 || LevelWords.Contains(key)) continue;
            tags.TryAdd(key, value);
        }

        // Bracketed tokens have no name of their own, so they're numbered by
        // position: the first is usually the category/logger, the second a
        // thread or correlation id.
        var bracketIndex = 0;
        foreach (Match m in BracketTokenRegex.Matches(line))
        {
            var token = m.Groups[1].Value.Trim();
            if (token.Length == 0 || LevelWords.Contains(token)) continue;
            if (HasTimestamp(token) || token.Contains('=')) continue;

            bracketIndex++;
            tags.TryAdd(bracketIndex == 1 ? "context" : $"context{bracketIndex}", token);
            if (bracketIndex >= 3) break;
        }

        return tags;
    }

    private static bool TryParseJsonLine(string line, string sourceFile, out LogEntry? entry)
    {
        entry = null;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            var result = new LogEntry { SourceFile = sourceFile, Message = line };

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var name = prop.Name;
                var text = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Null => "",
                    JsonValueKind.Object or JsonValueKind.Array => prop.Value.GetRawText(),
                    _ => prop.Value.ToString()
                };

                if (IsNamed(name, "timestamp", "time", "@t", "ts", "date", "datetime")
                    && TryParseTimestamp(text, out var ts))
                {
                    result.Timestamp = ts;
                }
                else if (IsNamed(name, "level", "@l", "severity", "loglevel", "lvl"))
                {
                    result.Level = MapLevel(text);
                }
                else if (IsNamed(name, "message", "@m", "msg", "messagetemplate", "@mt"))
                {
                    result.Message = text;
                }
                else if (text.Length > 0 && prop.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                {
                    result.Tags.TryAdd(name, text);
                }
            }

            // Serilog compact format omits "@l" entirely for Information.
            if (result.Level == LogSeverity.Unknown && doc.RootElement.TryGetProperty("@t", out _))
            {
                result.Level = LogSeverity.Info;
            }

            entry = result;
            return true;
        }
        catch (JsonException)
        {
            return false; // looked like JSON, wasn't - fall back to text parsing
        }
    }

    private static bool IsNamed(string actual, params string[] candidates) =>
        candidates.Any(c => string.Equals(actual, c, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseTimestamp(string text, out DateTime timestamp)
    {
        text = text.Trim().Replace(',', '.');

        // No AssumeLocal/AdjustToUniversal here on purpose: a timestamp written
        // without a timezone must keep its wall-clock value, otherwise a log
        // line reading 09:00 renders as 03:30 for anyone east of UTC. Formats
        // that *do* carry an offset are still converted normally.
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out timestamp))
        {
            return true;
        }

        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out timestamp))
        {
            return true;
        }

        // Last resort: whatever the server's own culture makes of it (covers
        // dd/MM vs MM/dd layouts written by a locale-aware framework).
        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out timestamp);
    }
}
