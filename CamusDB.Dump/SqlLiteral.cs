
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Globalization;
using System.Text;
using CamusDB.Client;

namespace CamusDB.Dump;

/// <summary>
/// Renders a <see cref="ColumnValue"/> as the SQL literal CamusDB parses back into the same value.
///
/// <para>Every type CamusDB stores has a literal form. Numbers and booleans are written directly;
/// <c>date</c>, <c>datetime</c> and <c>uuid</c> ride in as string literals the server coerces on
/// INSERT; <c>bytes</c> uses <c>X'…'</c> and arrays use <c>ARRAY[…]</c>. The only values with no
/// literal at all are the non-finite floats (NaN, ±Infinity), which are reported through
/// <see cref="DumpWarnings"/> rather than silently mangled.</para>
///
/// <para>The string rules mirror the server's <c>SqlStringLiteral</c> exactly and must be kept in
/// step with it by hand: this tool depends on <c>CamusDB.Client</c>, not <c>CamusDB.Core</c>, so it
/// cannot reference that type. A plain <c>'…'</c> literal does no escape processing (a backslash is
/// an ordinary character, a doubled quote is one quote), and only a value containing a control
/// character needs the <c>E'…'</c> escape form.</para>
/// </summary>
internal static class SqlLiteral
{
    /// <summary>
    /// Custom format that never falls back to scientific notation, which the CamusDB lexer does not
    /// accept (its float literal is strictly <c>digits.digits</c>). The leading <c>0.0</c> also
    /// guarantees the decimal point a whole-valued double would otherwise lose.
    /// </summary>
    private static readonly string PlainDoubleFormat = "0.0" + new string('#', 330);

    public static string Render(in ColumnValue value, string table, string column, DumpWarnings warnings)
    {
        switch (value.Type)
        {
            case ColumnType.Null:
                return "NULL";

            case ColumnType.Id:
                return value.StrValue is null
                    ? "NULL"
                    : "STR_ID(" + RenderString(value.StrValue) + ")";

            case ColumnType.String:
                return RenderString(value.StrValue ?? "");

            case ColumnType.Integer64:
                return value.LongValue.ToString(CultureInfo.InvariantCulture);

            case ColumnType.Float64:
                return RenderDouble(value.FloatValue, table, column, warnings);

            case ColumnType.Float32:
                return RenderDouble((float)value.FloatValue, table, column, warnings);

            case ColumnType.Bool:
                return value.BoolValue ? "true" : "false";

            // X'…' carries the bytes type on its own; a bare 0x… would read back as an integer.
            case ColumnType.Bytes:
                return "X'" + Convert.ToHexString(value.BytesValue ?? []) + "'";

            case ColumnType.Date:
                return "'" + Utc(value.LongValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";

            case ColumnType.DateTime:
                return RenderDateTime(value.LongValue, table, column, warnings);

            case ColumnType.Uuid:
                return "'" + value.AsGuid().ToString("D", CultureInfo.InvariantCulture) + "'";

            case ColumnType.Array:
                return RenderArray(value, table, column, warnings);

            default:
                warnings.Unrepresentable(table, column, $"unknown column type {value.Type}");
                return "NULL";
        }
    }

    /// <summary>
    /// Quotes a string so the server parses it back byte for byte.
    ///
    /// <para>The plain form carries almost everything: it does no escape processing, so a backslash,
    /// a regex, a Windows path, and the other quote character all survive verbatim, and the only
    /// special sequence is a doubled delimiter. Only a control character — which the lexer excludes
    /// from a plain literal outright — needs the <c>E'…'</c> escape form.</para>
    ///
    /// <para>This replaced a scheme that tried both quote styles and re-scanned each candidate with a
    /// hand-written emulation of the lexer. That emulation existed because some values had no literal
    /// at all; now that every value does, the emulation is gone and with it the risk of it drifting
    /// from the real scanner.</para>
    /// </summary>
    private static string RenderString(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
                return QuoteEscaped(value);
        }

        return "'" + value.Replace("'", "''") + "'";
    }

    /// <summary>
    /// Renders the <c>E'…'</c> escape form, used only for values holding a control character.
    /// Mirrors the escape set the server's decoder accepts.
    /// </summary>
    private static string QuoteEscaped(string value)
    {
        StringBuilder sb = new((value.Length * 6) + 3);

        sb.Append("E'");

        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\0': sb.Append("\\0"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\v': sb.Append("\\v"); break;
                default:
                    if (char.IsControl(c))
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }

        return sb.Append('\'').ToString();
    }

    /// <summary>
    /// Renders an <c>ARRAY[…]</c> literal. Elements reuse the scalar rendering, so a string element
    /// is quoted by the same rules and a bytes element uses <c>X'…'</c>.
    /// </summary>
    private static string RenderArray(in ColumnValue value, string table, string column, DumpWarnings warnings)
    {
        IReadOnlyList<ColumnValue> elements = value.ArrayValues ?? [];
        StringBuilder sb = new();

        sb.Append("ARRAY[");

        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");

            sb.Append(Render(elements[i], table, column, warnings));
        }

        return sb.Append(']').ToString();
    }

    private static string RenderDouble(double value, string table, string column, DumpWarnings warnings)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            warnings.Unrepresentable(table, column, $"'{value}' has no SQL literal form");
            return "NULL";
        }

        string round = value.ToString("R", CultureInfo.InvariantCulture);

        // "R" reaches for scientific notation on very large and very small magnitudes; the plain format
        // spells those out, since the lexer's float literal has no exponent form.
        if (round.Contains('E') || round.Contains('e'))
            return value.ToString(PlainDoubleFormat, CultureInfo.InvariantCulture);

        return round.Contains('.') ? round : round + ".0";
    }

    /// <summary>
    /// A datetime literal carries at most milliseconds, while the column stores 100-nanosecond ticks, so
    /// a finer-grained value is reported as truncated rather than passed off as a faithful dump.
    /// </summary>
    private static string RenderDateTime(long ticks, string table, string column, DumpWarnings warnings)
    {
        if (ticks % TimeSpan.TicksPerMillisecond != 0)
            warnings.Truncated(table, column, "datetime values are truncated to milliseconds — CamusDB literals carry no finer precision");

        return "'" + Utc(ticks).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "'";
    }

    private static DateTime Utc(long ticks) => new(ticks, DateTimeKind.Utc);

    /// <summary>
    /// Quotes an identifier for the dump, after it is checked against the CamusDB identifier grammar
    /// <c>[A-Za-z_][A-Za-z0-9_]*</c>.
    ///
    /// <para>The check is the point of this method. Every name here comes from the server — table names
    /// from <c>SHOW TABLES</c>, database names from <c>SHOW DATABASES</c>, index and column names from
    /// <c>SHOW INDEXES</c> and from the data reader — or from an option the operator typed. A dump file
    /// is a program: whatever lands in it runs at restore time, with the restoring client's rights, often
    /// against a more sensitive database than the source. A name such as <c>x`; DROP TABLE users; --</c>
    /// would otherwise close the quoting and carry its own statements across. The grammar was already
    /// assumed here; it is now enforced.</para>
    ///
    /// <para>Backticks in the name are doubled as well, so the quoting stays correct on its own terms if
    /// the grammar above is ever widened.</para>
    /// </summary>
    /// <param name="name">The identifier to quote.</param>
    /// <param name="kind">What the name names, used in the error message — "table", "column", and so on.</param>
    /// <exception cref="DumpException">The name is not a CamusDB identifier.</exception>
    public static string Identifier(string name, string kind)
    {
        RequirePlainIdentifier(name, kind);

        return "`" + name.Replace("`", "``") + "`";
    }

    /// <summary>
    /// Throws unless <paramref name="name"/> is a CamusDB identifier. Callers that emit a name without
    /// quoting it use this directly.
    /// </summary>
    /// <exception cref="DumpException">The name is not a CamusDB identifier.</exception>
    public static void RequirePlainIdentifier(string name, string kind)
    {
        if (IsPlainIdentifier(name))
            return;

        throw new DumpException(
            $"the server reported the {kind} '{name}', which is not a CamusDB identifier " +
            "([A-Za-z_][A-Za-z0-9_]*). It is refused rather than written into the dump, because a dump " +
            "runs as SQL when it is restored. Exclude the object with --exclude-table or --exclude-database.");
    }

    /// <summary>Whether a name matches the CamusDB identifier grammar <c>[A-Za-z_][A-Za-z0-9_]*</c>.</summary>
    public static bool IsPlainIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
            return false;

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }
}
