
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

namespace CamusDB.Dump;

/// <summary>
/// Thrown when <c>--strict</c> is set and a value cannot be written as a SQL literal.
/// </summary>
public sealed class DumpException : Exception
{
    public DumpException(string message) : base(message)
    {
    }
}

/// <summary>
/// Collects everything the dump could not reproduce exactly. A dump that quietly drops values is worse
/// than one that fails, so each distinct table/column problem is reported to standard error at the end
/// (and to the dump itself as a comment), and <c>--strict</c> turns the first one into a failure.
/// </summary>
public sealed class DumpWarnings
{
    private readonly bool strict;

    private readonly Dictionary<string, (string Message, long Count)> issues = new(StringComparer.Ordinal);

    public DumpWarnings(bool strict)
    {
        this.strict = strict;
    }

    public bool Any => issues.Count > 0;

    public void Unrepresentable(string table, string column, string reason)
    {
        string message = $"{table}.{column}: {reason}. The affected values were dumped as NULL.";

        if (strict)
            throw new DumpException(message);

        Record($"unrepresentable|{table}|{column}", message);
    }

    public void Truncated(string table, string column, string reason)
    {
        Record($"truncated|{table}|{column}", $"{table}.{column}: {reason}.");
    }

    private void Record(string key, string message)
    {
        issues[key] = issues.TryGetValue(key, out (string Message, long Count) existing)
            ? (message, existing.Count + 1)
            : (message, 1);
    }

    public IEnumerable<string> Summaries()
        => issues.Values
            .OrderBy(issue => issue.Message, StringComparer.Ordinal)
            .Select(issue => $"{issue.Message} ({issue.Count} value{(issue.Count == 1 ? "" : "s")})");

    /// <summary>
    /// Writes the collected warnings to standard error. <paramref name="database"/> names the database
    /// they came from, which only matters when several were dumped in one run and the same table name can
    /// appear in more than one of them.
    /// </summary>
    public void Report(TextWriter writer, string? database = null)
    {
        string scope = string.IsNullOrEmpty(database) ? "" : database + ": ";

        foreach (string summary in Summaries())
            writer.WriteLine("camus-dump: warning: " + scope + summary);
    }
}
