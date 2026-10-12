/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Globalization;
using System.Text.RegularExpressions;
using CamusDB.Client;

namespace CamusDB.Dump;

/// <summary>
/// One row of <c>SHOW SEQUENCES</c>.
/// </summary>
/// <param name="ReservedUpTo">
/// The sequence's position: no value above it has been issued. Null when nothing has been drawn since
/// the sequence was created or last moved.
/// </param>
/// <param name="OwnedBy">The owning column as <c>table.column</c>, or null for a free-standing sequence.</param>
internal sealed record SequenceInfo(
    string Name,
    long? ReservedUpTo,
    long Start,
    long Increment,
    long Min,
    long? Max,
    long Cache,
    string? OwnedBy,
    string? Comment)
{
    public string? OwnerTable => OwnedBy is null ? null : OwnedBy[..Math.Max(0, OwnedBy.LastIndexOf('.'))];

    public string? OwnerColumn => OwnedBy is null ? null : OwnedBy[(OwnedBy.LastIndexOf('.') + 1)..];
}

/// <summary>
/// Reads the sequences of a database and writes the SQL that restores them.
///
/// <para>A free-standing sequence is created with <c>CREATE SEQUENCE</c> before the first table, because a
/// column default can draw from it. A sequence owned by an identity or <c>serial</c> column is created by
/// that column's <c>CREATE TABLE</c>, so only its name, its options and its comment are restored, after
/// the table.</para>
///
/// <para>Each sequence's position is restored with <c>setval</c>, after the rows. A row restored with an
/// explicit value does not advance a sequence, so without it a restored sequence would hand out values
/// that restored rows already hold, and a unique key would refuse the insert. The position is read with
/// <c>SHOW SEQUENCES</c> when the dump starts. It has no time-travel form, so it is the current position,
/// which is never below the position at the instant the rows are read as of.</para>
/// </summary>
internal static partial class SequenceCatalog
{
    /// <summary>
    /// Lists the database's sequences, or returns null when the server has no <c>SHOW SEQUENCES</c>.
    /// </summary>
    public static async Task<List<SequenceInfo>?> ReadAsync(Func<string, CamusCommand> createCommand, CancellationToken cancellationToken)
    {
        try
        {
            using CamusCommand cmd = createCommand("SHOW SEQUENCES");
            using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            List<SequenceInfo> sequences = [];

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sequences.Add(new SequenceInfo(
                    Name: Text(reader, "sequence") ?? "",
                    ReservedUpTo: Number(reader, "reserved_upto"),
                    Start: Number(reader, "start_value") ?? 1,
                    Increment: Number(reader, "increment") ?? 1,
                    Min: Number(reader, "min_value") ?? 1,
                    Max: Number(reader, "max_value"),
                    Cache: Number(reader, "cache") ?? 1,
                    OwnedBy: Text(reader, "owned_by"),
                    Comment: Text(reader, "comment")));
            }

            return sequences;
        }
        catch (CamusException)
        {
            return null;
        }
    }

    /// <summary>
    /// The sequences a dump of <paramref name="tables"/> needs. Without a table filter that is every
    /// sequence. With one, it is the sequences the dumped tables own, and the free-standing sequences their
    /// column defaults draw from.
    /// </summary>
    public static List<SequenceInfo> Select(
        List<SequenceInfo> sequences,
        IReadOnlyCollection<string> tables,
        IReadOnlyDictionary<string, List<string>> definitions,
        bool filtered)
    {
        if (!filtered)
            return sequences;

        HashSet<string> included = new(tables, StringComparer.OrdinalIgnoreCase);
        HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);

        foreach (string table in tables)
        {
            if (!definitions.TryGetValue(table, out List<string>? ddls))
                continue;

            foreach (string ddl in ddls)
                foreach (Match match in NextvalCall().Matches(ddl))
                    referenced.Add(match.Groups["name"].Value.Replace("''", "'"));
        }

        return sequences
            .Where(s => s.OwnerTable is not null ? included.Contains(s.OwnerTable) : referenced.Contains(s.Name))
            .ToList();
    }

    /// <summary>The <c>CREATE SEQUENCE</c> for a free-standing sequence, with every option spelled out.</summary>
    public static string CreateStatement(SequenceInfo sequence, bool ifNotExists)
    {
        string sql = "CREATE SEQUENCE " + (ifNotExists ? "IF NOT EXISTS " : "") + SqlLiteral.Identifier(sequence.Name, "sequence")
            + " START WITH " + Integer(sequence.Start)
            + " INCREMENT BY " + Integer(sequence.Increment)
            + " MINVALUE " + Integer(sequence.Min)
            + (sequence.Max is long max ? " MAXVALUE " + Integer(max) : "")
            + " CACHE " + Integer(sequence.Cache);

        return sql + ";";
    }

    /// <summary>
    /// The statements that turn the sequence an identity column creates into the one the source has: the
    /// same name, the same options and the same comment. The restored table names its sequence
    /// <c>{table}_{column}_seq</c>, as the server does for a new identity column.
    /// </summary>
    public static IEnumerable<string> OwnedStatements(SequenceInfo sequence)
    {
        string generated = $"{sequence.OwnerTable}_{sequence.OwnerColumn}_seq";
        string name = SqlLiteral.Identifier(sequence.Name, "sequence");

        if (!string.Equals(generated, sequence.Name, StringComparison.Ordinal))
            yield return $"ALTER SEQUENCE {SqlLiteral.Identifier(generated, "sequence")} RENAME TO {name};";

        // An identity column starts at 1, steps by 1 and has no maximum. Anything else was set afterwards.
        if (sequence.Start != 1 || sequence.Increment != 1 || sequence.Min != 1 || sequence.Max is not null)
        {
            yield return $"ALTER SEQUENCE {name} MINVALUE {Integer(sequence.Min)} START WITH {Integer(sequence.Start)}"
                + $" INCREMENT BY {Integer(sequence.Increment)}"
                + (sequence.Max is long max ? $" MAXVALUE {Integer(max)}" : " NO MAXVALUE") + ";";
        }

        if (CommentStatement(sequence) is string comment)
            yield return comment;
    }

    public static string? CommentStatement(SequenceInfo sequence)
        => sequence.Comment is null
            ? null
            : $"COMMENT ON SEQUENCE {SqlLiteral.Identifier(sequence.Name, "sequence")} IS {SqlLiteral.Quote(sequence.Comment)};";

    /// <summary>
    /// Moves the restored sequence to the source's position, so its next value is the one the source
    /// would issue. Null for a sequence nothing has been drawn from: a new sequence already starts there.
    /// </summary>
    public static string? PositionStatement(SequenceInfo sequence)
    {
        if (sequence.ReservedUpTo is not long position)
            return null;

        SqlLiteral.RequirePlainIdentifier(sequence.Name, "sequence");

        return $"SELECT setval({SqlLiteral.Quote(sequence.Name)}, {Integer(position)});";
    }

    public static string DropStatement(SequenceInfo sequence)
        => $"DROP SEQUENCE IF EXISTS {SqlLiteral.Identifier(sequence.Name, "sequence")};";

    private static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static int? Ordinal(CamusDataReader reader, string field)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), field, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return null;
    }

    private static string? Text(CamusDataReader reader, string field)
    {
        if (Ordinal(reader, field) is not int i || reader.IsDBNull(i))
            return null;

        ColumnValue value = reader.GetColumnValue(i);

        return value.Type == ColumnType.Null ? null : value.StrValue ?? Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a number the server may send as an integer or as text.</summary>
    private static long? Number(CamusDataReader reader, string field)
    {
        if (Ordinal(reader, field) is not int i || reader.IsDBNull(i))
            return null;

        ColumnValue value = reader.GetColumnValue(i);

        return value.Type switch
        {
            ColumnType.Null => null,
            ColumnType.Integer64 => value.LongValue,
            _ => long.TryParse(value.StrValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null,
        };
    }

    /// <summary>A <c>nextval('name')</c> call in a column default, as SHOW CREATE TABLE writes it.</summary>
    [GeneratedRegex(@"nextval\(\s*'(?<name>(?:[^']|'')*)'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex NextvalCall();
}
