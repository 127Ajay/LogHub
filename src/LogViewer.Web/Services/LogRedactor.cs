using System.Text.RegularExpressions;
using LogViewer.Web.Models;

namespace LogViewer.Web.Services;

/// <summary>
/// Masks values that look like credentials before an entry leaves the server.
///
/// This is a safety net, not a control. The expectation set in the PRD is
/// still that source applications don't log secrets in the first place -
/// pattern matching cannot catch a secret written as a bare string with no
/// surrounding context, and nothing here should be read as a guarantee that
/// logs are safe to share. It catches the common accidental cases: a
/// connection string, a bearer token, an api_key=... query parameter.
///
/// Redaction is applied to what is sent to the browser. The underlying log
/// files are never modified - LogHub only ever opens them for reading.
/// </summary>
public static class LogRedactor
{
    private const string Mask = "***REDACTED***";

    // key=value / key: value / "key":"value" where the key names something
    // secret. The value form is deliberately broad (quoted, or up to the next
    // delimiter) because these show up in URLs, JSON and connection strings.
    private static readonly Regex SecretAssignment = new(
        @"(?<key>password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|auth|authorization|credential|connectionstring|conn[_-]?str)" +
        @"(?<sep>\s*[:=]\s*|""\s*:\s*"")" +
        // The negative lookahead matters: without it "Authorization: Bearer <token>"
        // matches with value="Bearer", masking the scheme word and leaving the
        // actual token in plain sight. Auth schemes are handled by BearerToken.
        @"(?!(?:Bearer|Basic)\b)(?<value>""[^""]*""|'[^']*'|[^\s,;&""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Authorization: Bearer eyJ..." and friends.
    private static readonly Regex BearerToken = new(
        @"(?<scheme>Bearer|Basic)\s+(?<value>[A-Za-z0-9\-._~+/]{8,}=*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A bare JWT sitting in a message with no key to identify it.
    private static readonly Regex Jwt = new(
        @"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*",
        RegexOptions.Compiled);

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd", "secret", "token", "api_key", "api-key", "apikey",
        "access_key", "access-key", "auth", "authorization", "credential",
        "connectionstring", "conn_str", "conn-str"
    };

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Order matters: scheme-prefixed tokens first, so the more general
        // key=value rule can't consume the scheme word and stop there.
        text = BearerToken.Replace(text, m => m.Groups["scheme"].Value + " " + Mask);
        text = Jwt.Replace(text, Mask);
        text = SecretAssignment.Replace(text, m =>
            m.Groups["key"].Value + m.Groups["sep"].Value + MaskPreservingQuotes(m.Groups["value"].Value));

        return text;
    }

    /// <summary>
    /// Masks an entry in place. Applied after grouping, so continuation lines
    /// folded into Message (stack traces, which routinely carry connection
    /// strings) are covered too - redacting inside LogLineParser.Parse would
    /// miss them entirely.
    /// </summary>
    public static LogEntry Apply(LogEntry entry)
    {
        entry.Message = Redact(entry.Message);

        if (entry.Tags.Count > 0)
        {
            foreach (var key in entry.Tags.Keys.ToList())
            {
                entry.Tags[key] = RedactTagValue(key, entry.Tags[key]);
            }
        }

        return entry;
    }

    /// <summary>True if a tag key names something that shouldn't be shown.</summary>
    public static bool IsSecretKey(string key) => SecretKeys.Contains(key);

    public static string RedactTagValue(string key, string value) =>
        IsSecretKey(key) ? Mask : Redact(value);

    // Keep the original quoting so a redacted JSON line stays valid JSON.
    private static string MaskPreservingQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return '"' + Mask + '"';
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return '\'' + Mask + '\'';
        return Mask;
    }
}
