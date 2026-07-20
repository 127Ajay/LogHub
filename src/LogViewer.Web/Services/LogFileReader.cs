namespace LogViewer.Web.Services;

/// <summary>
/// Reads log files that the owning application very likely still has open for
/// writing. File.ReadLines / File.ReadAllText open without FileShare.Write and
/// throw IOException against the handle most logging frameworks hold, which
/// previously made actively-written files - i.e. exactly the ones anyone wants
/// to look at - silently return nothing.
/// </summary>
public static class LogFileReader
{
    public static IEnumerable<string> ReadLinesShared(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line) yield return line;
    }
}
