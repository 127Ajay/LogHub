using System.Text.RegularExpressions;
using LogViewer.Web.Models;

namespace LogViewer.Web.Services;

/// <summary>
/// A parsed search expression, matched against a whole grouped LogEntry rather
/// than a raw physical line. That distinction matters: an entry's Message may
/// span many lines (a stack trace), and a naive per-line match would fail to
/// find a term that sits three lines into an exception. Anything added here
/// must keep matching entry.Message, not re-derive its own line splitting.
///
/// Supported forms, in the order they're tried:
///   - regex mode:   the whole query is one .NET regular expression
///   - keyword mode: space-separated terms, implicitly AND
///                   "exact phrase" for a term containing spaces
///                   OR / AND as explicit operators (OR binds looser)
///                   -term or NOT term to exclude
/// An empty/whitespace query matches everything.
/// </summary>
public sealed class LogQuery
{
    // OR groups of AND groups: (a AND b) OR (c). Matches if any group matches.
    private readonly List<List<Term>> _orGroups = new();
    private readonly Regex? _regex;
    private readonly bool _matchAll;

    public string? Error { get; }
    public bool IsValid => Error is null;

    private LogQuery(string? error) => Error = error;

    private LogQuery(Regex regex) => _regex = regex;

    private LogQuery(List<List<Term>> orGroups, bool matchAll)
    {
        _orGroups = orGroups;
        _matchAll = matchAll;
    }

    public static LogQuery Parse(string? query, bool useRegex)
    {
        if (string.IsNullOrWhiteSpace(query)) return new LogQuery(new List<List<Term>>(), matchAll: true);

        if (useRegex)
        {
            try
            {
                // Timeout guards against a user-supplied pattern that
                // backtracks catastrophically over a large log file.
                return new LogQuery(new Regex(query, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)));
            }
            catch (ArgumentException ex)
            {
                return new LogQuery("Invalid regular expression: " + ex.Message);
            }
        }

        var tokens = Tokenize(query);
        var orGroups = new List<List<Term>>();
        var currentAnd = new List<Term>();

        foreach (var token in tokens)
        {
            if (string.Equals(token.Text, "OR", StringComparison.OrdinalIgnoreCase) && !token.Quoted)
            {
                if (currentAnd.Count > 0) orGroups.Add(currentAnd);
                currentAnd = new List<Term>();
                continue;
            }

            // AND is the default, so an explicit one is just noise to drop.
            // (NOT never reaches here - Tokenize folds it into the next token.)
            if (string.Equals(token.Text, "AND", StringComparison.OrdinalIgnoreCase) && !token.Quoted) continue;

            var text = token.Text;
            var negate = token.Negated;
            if (!token.Quoted && text.StartsWith('-') && text.Length > 1)
            {
                negate = true;
                text = text[1..];
            }

            if (text.Length == 0) continue;
            currentAnd.Add(new Term(text, negate));
        }

        if (currentAnd.Count > 0) orGroups.Add(currentAnd);
        if (orGroups.Count == 0) return new LogQuery(new List<List<Term>>(), matchAll: true);

        return new LogQuery(orGroups, matchAll: false);
    }

    public bool Matches(LogEntry entry)
    {
        if (_matchAll) return true;

        var haystack = entry.Message;

        if (_regex is not null)
        {
            try { return _regex.IsMatch(haystack); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        foreach (var andGroup in _orGroups)
        {
            var all = true;
            foreach (var term in andGroup)
            {
                var hit = haystack.Contains(term.Text, StringComparison.OrdinalIgnoreCase);
                if (hit == term.Negate) { all = false; break; }
            }

            if (all) return true;
        }

        return false;
    }

    private static List<Token> Tokenize(string query)
    {
        var tokens = new List<Token>();
        var i = 0;
        var negateNext = false;

        while (i < query.Length)
        {
            if (char.IsWhiteSpace(query[i])) { i++; continue; }

            if (query[i] == '"')
            {
                var end = query.IndexOf('"', i + 1);
                if (end < 0) end = query.Length;
                var phrase = query[(i + 1)..Math.Min(end, query.Length)];
                tokens.Add(new Token(phrase, quoted: true) { Negated = negateNext });
                negateNext = false;
                i = end + 1;
                continue;
            }

            var start = i;
            while (i < query.Length && !char.IsWhiteSpace(query[i])) i++;
            var word = query[start..i];

            if (string.Equals(word, "NOT", StringComparison.OrdinalIgnoreCase))
            {
                negateNext = true;
                continue;
            }

            tokens.Add(new Token(word, quoted: false) { Negated = negateNext });
            negateNext = false;
        }

        return tokens;
    }

    private sealed class Token
    {
        public Token(string text, bool quoted) { Text = text; Quoted = quoted; }
        public string Text { get; }
        public bool Quoted { get; }
        public bool Negated { get; set; }
    }

    private readonly record struct Term(string Text, bool Negate);
}
