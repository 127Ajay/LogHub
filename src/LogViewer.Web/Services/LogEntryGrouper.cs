using LogViewer.Web.Models;

namespace LogViewer.Web.Services;

/// <summary>
/// Groups raw file lines into LogEntry records, merging continuation lines
/// (stack traces, wrapped messages - anything without its own timestamp)
/// into the entry they belong to, rather than treating every physical line
/// as an independent entry.
///
/// This is what keeps the app working across log formats it's never seen
/// before: a framework that writes multi-line entries (a message on its own
/// line, an exception spanning several lines, etc.) doesn't break parsing -
/// it just gets grouped under the timestamp that started it. Files with no
/// recognizable timestamps at all fall back to one line = one entry, so
/// plain unstructured logs still display sensibly.
/// </summary>
public static class LogEntryGrouper
{
    public static List<LogEntry> Group(IEnumerable<string> lines, string sourceFile)
    {
        var entries = new List<LogEntry>();
        var nonEmptyLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        // Only treat "no timestamp on this line" as a continuation if the file
        // demonstrably uses timestamps somewhere - otherwise every line would
        // collapse into one giant entry for logs that never have timestamps.
        var usesTimestamps = nonEmptyLines.Any(LogLineParser.HasTimestamp);

        LogEntry? current = null;

        foreach (var line in nonEmptyLines)
        {
            var isNewEntry = !usesTimestamps || LogLineParser.HasTimestamp(line);

            if (isNewEntry || current is null)
            {
                if (current is not null) entries.Add(current);
                current = LogLineParser.Parse(line, sourceFile);
            }
            else
            {
                current.Message += Environment.NewLine + line;
            }
        }

        if (current is not null) entries.Add(current);
        return entries;
    }
}
