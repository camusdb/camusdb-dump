
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Text;

namespace CamusDB.Dump;

/// <summary>
/// Reads SQL text that the server produced — today only the <c>CREATE TABLE</c> statement from
/// <c>SHOW CREATE TABLE</c> — and prepares it for the dump file.
///
/// <para>This text is the one thing a dump copies through without quoting it, because it is SQL by
/// definition. Two jobs are done on it here. <see cref="FindStatementSeparator"/> finds the statement
/// terminator, so the caller can hold the text to a single statement.
/// <see cref="TryMakeLiteralsUnambiguous"/> rewrites any string literal whose end depends on which
/// escape grammar the reader applies.</para>
///
/// <para>The second job exists for the same reason <see cref="SqlLiteral.Quote"/> avoids the plain form
/// for a value holding a backslash. The server writes a <c>DEFAULT</c> or a <c>COMMENT</c> in the plain
/// form, where a backslash is an ordinary character, so a comment that ends with a backslash reaches the
/// dump as <c>'…\'</c>. A loader that reads <c>\'</c> as an escaped quote never closes that string, and
/// the load fails on a statement further down the file. Rewriting the literal into the <c>E'…'</c> form
/// with every backslash doubled keeps the value identical for the server and ends the literal at the
/// same character for both kinds of reader.</para>
/// </summary>
internal static class SqlText
{
    /// <summary>
    /// Rewrites every plain string literal that holds a backslash into the equivalent <c>E'…'</c>
    /// literal. Everything else — keywords, identifiers, comments, numbers, literals already in the
    /// escape form — is copied character for character.
    ///
    /// <para>The rewrite needs no decoding step, which is what keeps it exact. A plain body and an
    /// escape-form body differ in one rule only: the escape form reads a backslash as an escape. So the
    /// plain body with each backslash doubled is the escape-form body with the same value. A doubled
    /// quote stays a doubled quote, because both forms map that pair to one quote.</para>
    /// </summary>
    /// <param name="sql">The statement to prepare.</param>
    /// <param name="rewritten">The prepared statement, or <paramref name="sql"/> when nothing changed.</param>
    /// <param name="problem">Why the text was refused, or null when it was accepted.</param>
    /// <returns>True when the text was accepted.</returns>
    public static bool TryMakeLiteralsUnambiguous(string sql, out string rewritten, out string? problem)
    {
        StringBuilder? sb = null;

        // Where the copy-through has reached. Everything before it is already in sb.
        int copied = 0;
        int i = 0;

        while (i < sql.Length)
        {
            char c = sql[i];

            // A line comment runs to the newline, and an unterminated one ends the text harmlessly.
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int newline = sql.IndexOf('\n', i);
                i = newline < 0 ? sql.Length : newline + 1;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);

                if (close < 0)
                {
                    rewritten = sql;
                    problem = "it leaves a quoted string or a comment open";
                    return false;
                }

                i = close + 2;
                continue;
            }

            if (c == '`')
            {
                int close = SkipDelimited(sql, i, '`', backslashEscapes: false);

                if (close < 0)
                {
                    rewritten = sql;
                    problem = "it leaves a quoted string or a comment open";
                    return false;
                }

                // A backtick-quoted name is a plain identifier in the CamusDB lexer, so a backslash
                // inside one is not something the server can have produced. It is refused rather than
                // carried, because a name is quoted with backticks in the dump and a loader that reads
                // a backslash as an escape would end the name somewhere else.
                if (sql.AsSpan(i + 1, close - i - 2).Contains('\\'))
                {
                    rewritten = sql;
                    problem = "a backtick-quoted name in it holds a backslash";
                    return false;
                }

                i = close;
                continue;
            }

            if (c is '"' or '\'')
            {
                char prefix = PrefixBefore(sql, i);
                int close = SkipDelimited(sql, i, c, backslashEscapes: prefix is 'E');

                if (close < 0)
                {
                    rewritten = sql;
                    problem = "it leaves a quoted string or a comment open";
                    return false;
                }

                ReadOnlySpan<char> body = sql.AsSpan(i + 1, close - i - 2);

                if (body.Contains('\\'))
                {
                    // A bytes literal holds hex digits only, so a backslash in one means the text is not
                    // what it claims to be. There is nothing safe to rewrite it into.
                    if (prefix is 'X')
                    {
                        rewritten = sql;
                        problem = "a bytes literal in it holds a backslash";
                        return false;
                    }

                    if (prefix is '\0')
                    {
                        sb ??= new StringBuilder(sql.Length + 16);
                        sb.Append(sql, copied, i - copied);
                        AppendAsEscapeForm(sb, body, c);
                        copied = close;
                    }
                }

                i = close;
                continue;
            }

            i++;
        }

        if (sb is null)
        {
            rewritten = sql;
            problem = null;
            return true;
        }

        sb.Append(sql, copied, sql.Length - copied);

        rewritten = sb.ToString();
        problem = null;
        return true;
    }

    /// <summary>
    /// Appends the escape-form literal that carries the plain-form <paramref name="body"/>: the same
    /// characters, with every backslash doubled, between an <c>E</c> and the delimiter it was written
    /// with.
    /// </summary>
    private static void AppendAsEscapeForm(StringBuilder sb, ReadOnlySpan<char> body, char delimiter)
    {
        sb.Append('E').Append(delimiter);

        foreach (char c in body)
        {
            if (c == '\\')
                sb.Append('\\');

            sb.Append(c);
        }

        sb.Append(delimiter);
    }

    /// <summary>
    /// The literal prefix that applies to the delimiter at <paramref name="quote"/>: <c>E</c> for the
    /// escape form, <c>X</c> for a bytes literal, or <c>\0</c> for a plain literal. The letter counts
    /// as a prefix only when it does not itself continue an identifier, so the <c>x</c> of
    /// <c>max'…'</c> is not read as one. Case is folded, so <c>e</c> and <c>E</c> both report <c>E</c>.
    /// </summary>
    private static char PrefixBefore(string sql, int quote)
    {
        if (quote == 0)
            return '\0';

        char letter = char.ToUpperInvariant(sql[quote - 1]);

        if (letter is not ('E' or 'X'))
            return '\0';

        if (quote >= 2 && (char.IsAsciiLetterOrDigit(sql[quote - 2]) || sql[quote - 2] == '_'))
            return '\0';

        return letter;
    }

    /// <summary>
    /// The index of the first <c>;</c> in <paramref name="sql"/> that stands outside every string
    /// literal, quoted identifier and comment, or -1 when there is none. <paramref name="unterminated"/>
    /// reports a literal or a block comment that is never closed, which is itself a reason to refuse the
    /// text: the rest of it cannot be scanned.
    /// </summary>
    public static int FindStatementSeparator(string sql, out bool unterminated)
    {
        unterminated = false;

        int i = 0;

        while (i < sql.Length)
        {
            char c = sql[i];

            if (c == ';')
                return i;

            // A line comment runs to the newline, and an unterminated one ends the text harmlessly.
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int newline = sql.IndexOf('\n', i);
                i = newline < 0 ? sql.Length : newline + 1;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);

                if (close < 0)
                {
                    unterminated = true;
                    return -1;
                }

                i = close + 2;
                continue;
            }

            if (c is '`' or '"' or '\'')
            {
                // Only the E'…' form reads a backslash as an escape; a plain literal takes it verbatim.
                i = SkipDelimited(sql, i, c, backslashEscapes: c != '`' && PrefixBefore(sql, i) is 'E');

                if (i < 0)
                {
                    unterminated = true;
                    return -1;
                }

                continue;
            }

            i++;
        }

        return -1;
    }

    /// <summary>
    /// The index just past the delimiter that closes the run starting at <paramref name="start"/>, or -1
    /// when it is never closed. A doubled delimiter stands for one character and does not close the run.
    /// </summary>
    private static int SkipDelimited(string sql, int start, char delimiter, bool backslashEscapes)
    {
        int i = start + 1;

        while (i < sql.Length)
        {
            char c = sql[i];

            if (backslashEscapes && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == delimiter)
            {
                if (i + 1 < sql.Length && sql[i + 1] == delimiter)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return -1;
    }
}
