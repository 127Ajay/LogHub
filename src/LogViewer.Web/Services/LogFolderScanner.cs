using System.Text.RegularExpressions;
using LogViewer.Web.Models;

namespace LogViewer.Web.Services;

/// <summary>
/// Walks an application's configured root folder(s) and builds a date -> files
/// index, without assuming a single folder convention. The walk is recursive
/// and each file's date is resolved by falling back through progressively
/// weaker signals:
///   1. a date in the file name      - app-2026-07-20.log, app_20260720.log
///   2. a date in the folder path    - Root\2026-07-20\, Root\2026\07\20\
///   3. the file's last-write time   - anything else, including plain app.log
/// Because of (3) every readable file lands somewhere in the index, so a
/// layout nobody anticipated still shows up in History rather than silently
/// producing "No log data found".
///
/// The index is rebuilt on demand rather than persisted anywhere, since the
/// application deliberately keeps no database - the log files themselves
/// are the only source of truth.
/// </summary>
public class LogFolderScanner
{
    // Root\2026-07-20\ or Root\2026_07_20\ or Root\20260720\
    private static readonly Regex DateFolderRegex = new(
        @"^(\d{4})[-_]?(\d{2})[-_]?(\d{2})$", RegexOptions.Compiled);

    // Year and month folders, for Root\2026\07\20\ style nesting.
    private static readonly Regex YearFolderRegex = new(@"^(\d{4})$", RegexOptions.Compiled);
    private static readonly Regex TwoDigitFolderRegex = new(@"^(\d{2})$", RegexOptions.Compiled);

    // A date anywhere in a file name: app-2026-07-20.log, app.20260720.log
    private static readonly Regex DateInNameRegex = new(
        @"(\d{4})[-_.]?(\d{2})[-_.]?(\d{2})", RegexOptions.Compiled);

    private static readonly string[] LogExtensions =
    {
        ".log", ".txt", ".json", ".jsonl", ".ndjson", ".out", ".err", ".trace"
    };

    // Rotated files keep the real extension in the middle: app.log.1,
    // app.log.20260720, app.log.gz is skipped (compressed, not readable as text).
    private static readonly Regex RotatedLogRegex = new(
        @"\.(log|txt|out|err)\.[\w-]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int MaxDepth = 6;

    private readonly ILogger<LogFolderScanner> _logger;
    private readonly string _contentRoot;

    public LogFolderScanner(IWebHostEnvironment env, ILogger<LogFolderScanner> logger)
    {
        _logger = logger;
        _contentRoot = env.ContentRootPath;
    }

    public Dictionary<DateOnly, List<string>> BuildIndex(LogApplicationConfig app)
    {
        var index = new Dictionary<DateOnly, List<string>>();

        foreach (var rawRoot in app.RootPaths)
        {
            var root = ResolveRoot(rawRoot);
            if (!Directory.Exists(root))
            {
                _logger.LogWarning("Configured root path {Root} for {App} does not exist", root, app.Name);
                continue;
            }

            IndexDirectory(root, root, index, depth: 0);
        }

        return index;
    }

    public List<string> GetFilesForDate(LogApplicationConfig app, DateOnly date)
    {
        var index = BuildIndex(app);
        return index.TryGetValue(date, out var files) ? files : new List<string>();
    }

    public List<string> GetFilesForToday(LogApplicationConfig app) =>
        GetFilesForDate(app, DateOnly.FromDateTime(DateTime.Now));

    /// <summary>
    /// Relative root paths are resolved against the content root, not the
    /// process working directory - those differ when the app runs as a Windows
    /// service or under IIS, which would otherwise make a working config
    /// silently resolve to nothing in production.
    /// </summary>
    private string ResolveRoot(string root) =>
        Path.IsPathRooted(root) ? root : Path.GetFullPath(root, _contentRoot);

    private void IndexDirectory(string dir, string root, Dictionary<DateOnly, List<string>> index, int depth)
    {
        foreach (var file in SafeEnumerateFiles(dir))
        {
            if (!IsLogFile(file)) continue;

            var date = InferDateFromFileName(file)
                       ?? InferDateFromPath(file, root)
                       ?? SafeLastWriteDate(file);

            if (date is not null) AddToIndex(index, date.Value, file);
        }

        if (depth >= MaxDepth) return;

        foreach (var sub in SafeEnumerateDirectories(dir))
        {
            IndexDirectory(sub, root, index, depth + 1);
        }
    }

    private static bool IsLogFile(string file)
    {
        var ext = Path.GetExtension(file);
        if (LogExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return true;
        return RotatedLogRegex.IsMatch(Path.GetFileName(file));
    }

    private IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list files in {Dir}", dir);
            return Enumerable.Empty<string>();
        }
    }

    private IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list subfolders in {Dir}", dir);
            return Enumerable.Empty<string>();
        }
    }

    private DateOnly? SafeLastWriteDate(string file)
    {
        try
        {
            return DateOnly.FromDateTime(File.GetLastWriteTime(file));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stat {File}", file);
            return null;
        }
    }

    private static void AddToIndex(Dictionary<DateOnly, List<string>> index, DateOnly date, string file)
    {
        if (!index.TryGetValue(date, out var list))
        {
            list = new List<string>();
            index[date] = list;
        }

        if (!list.Contains(file)) list.Add(file);
    }

    private static bool TryBuildDate(string year, string month, string day, out DateOnly date)
    {
        date = default;
        if (!int.TryParse(year, out var y)) return false;
        if (!int.TryParse(month, out var m)) return false;
        if (!int.TryParse(day, out var d)) return false;

        try
        {
            date = new DateOnly(y, m, d);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static DateOnly? InferDateFromFileName(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var match = DateInNameRegex.Match(name);
        if (!match.Success) return null;
        return TryBuildDate(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, out var date)
            ? date
            : null;
    }

    /// <summary>
    /// Reads the date out of the folders between the root and the file.
    /// Handles a single dated folder (2026-07-20) and split year/month/day
    /// nesting (2026\07\20), checking the closest folders to the file first.
    /// </summary>
    private static DateOnly? InferDateFromPath(string filePath, string root)
    {
        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(filePath) ?? root);
        if (relative == ".") return null;

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(s => s.Length > 0)
            .ToList();

        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var match = DateFolderRegex.Match(segments[i]);
            if (match.Success &&
                TryBuildDate(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, out var date))
            {
                return date;
            }

            // year\month\day spread across three folders
            if (i >= 2 &&
                YearFolderRegex.IsMatch(segments[i - 2]) &&
                TwoDigitFolderRegex.IsMatch(segments[i - 1]) &&
                TwoDigitFolderRegex.IsMatch(segments[i]) &&
                TryBuildDate(segments[i - 2], segments[i - 1], segments[i], out var nested))
            {
                return nested;
            }
        }

        return null;
    }
}
