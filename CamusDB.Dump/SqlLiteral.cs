
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
/// <para>The string rules mirror the server's <c>SqlStringLiteral</c> and must be kept in step with it
/// by hand: this tool depends on <c>CamusDB.Client</c>, not <c>CamusDB.Core</c>, so it cannot reference
/// that type. <see cref="Quote"/> documents the one place the two differ on purpose.</para>
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
                    : "STR_ID(" + Quote(value.StrValue) + ")";

            case ColumnType.String:
                return Quote(value.StrValue ?? "");

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
    /// Quotes a string so that the server parses it back character for character, in a spelling that
    /// every statement splitter ends at the same character.
    ///
    /// <para>The server has two literal forms. The plain form <c>'…'</c> does no escape processing at
    /// all: a backslash is an ordinary character, and the only special sequence is a doubled quote. The
    /// escape form <c>E'…'</c> reads a backslash as an escape, which is how a control character gets a
    /// spelling. <see cref="NeedsEscapeForm"/> decides between them.</para>
    ///
    /// <para><b>Why a backslash alone selects the escape form.</b> The server is not the first reader of
    /// a dump. The loading client reads it first, and cuts the file into statements at the <c>;</c>
    /// characters that stand outside a string. Such a splitter has to decide what closes a string, and
    /// camus-cli reads <c>\'</c> as an escaped quote in every literal, while the server reads it as a
    /// backslash followed by the closing quote. A value that ends with a backslash therefore has no
    /// plain spelling the two agree on. <c>'…\'</c> closes the string for the server and leaves it open
    /// for the splitter, which then swallows the statement terminator and merges the next statement into
    /// this one — the CADB0406 syntax error seen on a load. A doubled backslash is not a repair either,
    /// because the plain form has no escapes and would store both characters.</para>
    ///
    /// <para>The escape form has a spelling that both readings end at the same character, and that
    /// spelling is the invariant this file rests on: <b>inside <c>E'…'</c> a backslash is always
    /// doubled, and a quote is always written <c>''</c>, never <c>\'</c></b>. A reader that honours
    /// backslash escapes consumes <c>\\</c> as one escape, so it never meets a backslash beside a quote.
    /// A reader that honours no backslash escapes sees only doubled quotes. Both stop at the same
    /// closing quote. The server reads the <c>E</c> prefix and decodes the value exactly: its lexer
    /// admits a doubled quote inside the escape form (the <c>EscStringSingle</c> production), and its
    /// decoder maps that pair to one quote.</para>
    ///
    /// <para>A value with no backslash and no control character keeps the plain form, where the two
    /// readings cannot differ: with no backslash in the body, a backslash rule has nothing to act on.
    /// </para>
    /// </summary>
    public static string Quote(string value)
        => NeedsEscapeForm(value) ? QuoteEscaped(value) : QuotePlain(value);

    /// <summary>
    /// True when the value needs the <c>E'…'</c> form. Two kinds of character put it there: a control
    /// character, which the plain literal body excludes outright, and a backslash, which the readers of
    /// a dump disagree about (see <see cref="Quote"/>).
    /// </summary>
    private static bool NeedsEscapeForm(string value)
    {
        foreach (char c in value)
        {
            if (c == '\\' || char.IsControl(c))
                return true;
        }

        return false;
    }

    private static string QuotePlain(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Renders the <c>E'…'</c> escape form. The escape set is the one the server's decoder accepts, with
    /// two deliberate restrictions.
    ///
    /// <list type="bullet">
    ///   <item>A quote is written <c>''</c> and never <c>\'</c>, so that the literal ends at the same
    ///     character under either escape grammar. <see cref="Quote"/> carries the full reason.</item>
    ///   <item>NUL is written <c>\u0000</c> and never <c>\0</c>. The decoder tries octal before the named
    ///     escapes, so a NUL followed by two octal digits — <c>\0</c> then <c>12</c> — reads back as the
    ///     single octal escape <c>\012</c>, which is a line feed. No other named escape can be extended
    ///     that way.</item>
    /// </list>
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
                case '\'': sb.Append("''"); break;
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
