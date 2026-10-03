/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Text;

namespace CamusDB.Dump;

/// <summary>
/// One foreign key, as the <c>CREATE TABLE</c> of the child table declares it.
/// </summary>
/// <param name="ChildColumns">The referencing columns, in constraint order.</param>
/// <param name="ParentTable">The referenced table. It is the child table itself for a self-reference.</param>
/// <param name="ParentColumns">The referenced columns, in constraint order. Empty when the clause names
/// the table only, which means its primary key.</param>
internal sealed record ForeignKeyReference(string[] ChildColumns, string ParentTable, string[] ParentColumns);

/// <summary>
/// Puts a dump in the order that a loader with enforced foreign keys accepts: each table after the
/// tables it references, and, in a table that references itself, each row after the row it references.
///
/// <para>CamusDB checks a foreign key when the statement that writes the child row ends. So a child
/// table has to be created after its parent, a child row has to come in a later <c>INSERT</c> than its
/// parent row or in the same one, and a parent table can be dropped only after every child table.
/// The server refuses a reference cycle between tables, so the tables always have an order. A
/// self-reference is permitted, and the rows of such a table can still form a cycle; those rows go
/// into one statement.</para>
///
/// <para>The server stores a foreign key as ids and reports it only in <c>SHOW CREATE TABLE</c>, so
/// the constraints are read from that text. A server that does not render them gives no references,
/// and the dump keeps the order <c>SHOW TABLES</c> returned.</para>
/// </summary>
internal static class ForeignKeyOrder
{
    /// <summary>
    /// The foreign keys that a <c>CREATE TABLE</c> statement declares, in the table form
    /// <c>FOREIGN KEY (…) REFERENCES t (…)</c> and in the column form <c>c … REFERENCES t (…)</c>.
    /// String literals, comments and backtick-quoted names are skipped, so a <c>REFERENCES</c> inside a
    /// default, a comment or a column name is not read as a constraint.
    /// </summary>
    public static List<ForeignKeyReference> Parse(string ddl)
    {
        List<Token> tokens = Tokenize(ddl);
        List<ForeignKeyReference> references = [];

        int depth = 0;

        // The column that the current element of the table body defines, for the column form.
        string? column = null;

        // The referencing columns of a FOREIGN KEY clause, until its REFERENCES is read.
        string[]? pending = null;

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];

            if (token.Kind == TokenKind.Open)
            {
                depth++;

                if (depth == 1)
                    column = ElementName(tokens, i + 1);

                continue;
            }

            if (token.Kind == TokenKind.Close)
            {
                depth--;
                continue;
            }

            if (token.Kind == TokenKind.Comma)
            {
                if (depth == 1)
                {
                    column = ElementName(tokens, i + 1);
                    pending = null;
                }

                continue;
            }

            if (depth != 1)
                continue;

            if (token.IsWord("FOREIGN") && i + 2 < tokens.Count && tokens[i + 1].IsWord("KEY") && tokens[i + 2].Kind == TokenKind.Open)
            {
                pending = ReadNameList(tokens, i + 2, out int end);
                i = end;
                continue;
            }

            if (!token.IsWord("REFERENCES"))
                continue;

            if (!TryReadTableName(tokens, i + 1, out string? parent, out int next))
                continue;

            string[] parentColumns = [];

            if (next < tokens.Count && tokens[next].Kind == TokenKind.Open)
            {
                parentColumns = ReadNameList(tokens, next, out int end);
                next = end + 1;
            }

            string[] childColumns = pending ?? (column is null ? [] : [column]);

            references.Add(new ForeignKeyReference(childColumns, parent!, parentColumns));

            pending = null;
            i = next - 1;
        }

        return references;
    }

    /// <summary>
    /// The tables in an order where each one comes after every table it references. Among the tables
    /// that are free to go next, the one earlier in <paramref name="tables"/> goes first, so a database
    /// with no foreign keys keeps its order. A reference to a table outside the dump, and a
    /// self-reference, put no condition on the order.
    /// </summary>
    /// <param name="cycle">The tables that a reference cycle holds back, in their original order. They
    /// are put at the end. The server refuses such a cycle, so this is empty unless the schema changed
    /// while the definitions were read.</param>
    public static List<string> OrderTables(
        IReadOnlyList<string> tables,
        IReadOnlyDictionary<string, List<ForeignKeyReference>> references,
        out List<string> cycle)
    {
        HashSet<string> included = new(tables, StringComparer.OrdinalIgnoreCase);
        HashSet<string> placed = new(StringComparer.OrdinalIgnoreCase);
        List<string> ordered = new(tables.Count);

        bool Ready(string table)
            => !references.TryGetValue(table, out List<ForeignKeyReference>? list)
               || list.All(r => string.Equals(r.ParentTable, table, StringComparison.OrdinalIgnoreCase)
                                || !included.Contains(r.ParentTable)
                                || placed.Contains(r.ParentTable));

        // The number of tables is small, so a scan per placement is simpler than a graph with in-degrees.
        while (ordered.Count < tables.Count)
        {
            string? next = tables.FirstOrDefault(t => !placed.Contains(t) && Ready(t));

            if (next is null)
                break;

            placed.Add(next);
            ordered.Add(next);
        }

        cycle = tables.Where(t => !placed.Contains(t)).ToList();
        ordered.AddRange(cycle);

        return ordered;
    }

    /// <summary>
    /// The rows of a table that references itself, in an order where each row comes after the rows it
    /// references. Among the rows that are free to go next, the one read earlier goes first.
    ///
    /// <para>A row references another row when its referencing columns are all non-NULL and equal to
    /// the referenced columns of that row. A NULL in any referencing column means no reference, as
    /// under <c>MATCH SIMPLE</c>. Values are compared as the SQL literals the dump writes, which are
    /// equal exactly when the values are equal. A referenced row that is not in the rows read — a
    /// <c>--where</c> can leave it out — puts no condition on the order.</para>
    /// </summary>
    /// <param name="rows">The rendered literals of each row, in the order they were read.</param>
    /// <param name="keys">For each self-reference, the ordinals of its referencing columns and of its
    /// referenced columns.</param>
    /// <param name="held">The number of rows at the end of the result that a cycle of references holds
    /// back. They have no order that separate statements accept, so the caller writes them as one
    /// statement: the server checks the constraint when the statement ends.</param>
    public static List<int> OrderRows(IReadOnlyList<string[]> rows, IReadOnlyList<(int[] Child, int[] Parent)> keys, out int held)
    {
        int count = rows.Count;
        int[] waiting = new int[count];
        List<int>?[] children = new List<int>?[count];

        foreach ((int[] child, int[] parent) in keys)
        {
            Dictionary<string, int> byKey = new(StringComparer.Ordinal);

            for (int row = 0; row < count; row++)
            {
                if (KeyOf(rows[row], parent) is { } key)
                    byKey.TryAdd(key, row);
            }

            for (int row = 0; row < count; row++)
            {
                if (KeyOf(rows[row], child) is not { } key || !byKey.TryGetValue(key, out int referenced) || referenced == row)
                    continue;

                (children[referenced] ??= []).Add(row);
                waiting[row]++;
            }
        }

        PriorityQueue<int, int> ready = new();

        for (int row = 0; row < count; row++)
        {
            if (waiting[row] == 0)
                ready.Enqueue(row, row);
        }

        List<int> ordered = new(count);

        while (ready.TryDequeue(out int row, out _))
        {
            ordered.Add(row);

            if (children[row] is not { } dependents)
                continue;

            foreach (int dependent in dependents)
            {
                if (--waiting[dependent] == 0)
                    ready.Enqueue(dependent, dependent);
            }
        }

        held = count - ordered.Count;

        for (int row = 0; row < count && ordered.Count < count; row++)
        {
            if (waiting[row] > 0)
                ordered.Add(row);
        }

        return ordered;
    }

    /// <summary>
    /// The values of <paramref name="ordinals"/> in a row as one comparable key, or null when any of
    /// them is NULL. Each literal is prefixed with its length, so no two lists of literals give the
    /// same key.
    /// </summary>
    private static string? KeyOf(string[] row, int[] ordinals)
    {
        StringBuilder sb = new();

        foreach (int ordinal in ordinals)
        {
            string literal = row[ordinal];

            if (literal == "NULL")
                return null;

            sb.Append(literal.Length).Append(':').Append(literal);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The column name that begins the element of the table body at <paramref name="start"/>, or null
    /// when the element is a table constraint or an index.
    /// </summary>
    private static string? ElementName(List<Token> tokens, int start)
    {
        if (start >= tokens.Count)
            return null;

        Token token = tokens[start];

        if (token.Kind == TokenKind.Quoted)
            return token.Text;

        if (token.Kind != TokenKind.Word)
            return null;

        return token.Text.ToUpperInvariant() switch
        {
            "CONSTRAINT" or "PRIMARY" or "UNIQUE" or "KEY" or "INDEX" or "FOREIGN" or "CHECK" => null,
            _ => token.Text,
        };
    }

    /// <summary>
    /// Reads a table name at <paramref name="start"/>. A database qualifier is dropped, because a
    /// foreign key cannot cross databases.
    /// </summary>
    private static bool TryReadTableName(List<Token> tokens, int start, out string? name, out int next)
    {
        name = null;
        next = start;

        if (start >= tokens.Count || tokens[start].Kind is not (TokenKind.Word or TokenKind.Quoted))
            return false;

        name = tokens[start].Text;
        next = start + 1;

        if (next + 1 < tokens.Count && tokens[next].Kind == TokenKind.Dot && tokens[next + 1].Kind is TokenKind.Word or TokenKind.Quoted)
        {
            name = tokens[next + 1].Text;
            next += 2;
        }

        return true;
    }

    /// <summary>
    /// Reads the names in the parenthesized list that opens at <paramref name="open"/>.
    /// <paramref name="end"/> is the index of the closing parenthesis.
    /// </summary>
    private static string[] ReadNameList(List<Token> tokens, int open, out int end)
    {
        List<string> names = [];

        int i = open + 1;

        while (i < tokens.Count && tokens[i].Kind != TokenKind.Close)
        {
            if (tokens[i].Kind is TokenKind.Word or TokenKind.Quoted)
                names.Add(tokens[i].Text);

            i++;
        }

        end = i;

        return [.. names];
    }

    private enum TokenKind
    {
        Word,
        Quoted,
        Open,
        Close,
        Comma,
        Dot,
        Other,
    }

    private readonly record struct Token(TokenKind Kind, string Text)
    {
        public bool IsWord(string word) => Kind == TokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits the statement into the tokens <see cref="Parse"/> needs. String literals, numbers and
    /// comments become <see cref="TokenKind.Other"/>. Text that leaves a literal open ends the scan;
    /// the caller refuses such a definition before it writes it, so nothing is lost.
    /// </summary>
    private static List<Token> Tokenize(string sql)
    {
        List<Token> tokens = [];

        int i = 0;

        while (i < sql.Length)
        {
            char c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

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
                    break;

                i = close + 2;
                continue;
            }

            if (c == '`')
            {
                int close = SqlText.SkipDelimited(sql, i, '`', backslashEscapes: false);

                if (close < 0)
                    break;

                tokens.Add(new Token(TokenKind.Quoted, sql[(i + 1)..(close - 1)].Replace("``", "`")));
                i = close;
                continue;
            }

            if (c is '"' or '\'')
            {
                char prefix = SqlText.PrefixBefore(sql, i);
                int close = SqlText.SkipDelimited(sql, i, c, backslashEscapes: prefix is 'E');

                if (close < 0)
                    break;

                // The prefix letter of an E'…' or an X'…' literal was read as a word just before.
                if (prefix is not '\0')
                    tokens.RemoveAt(tokens.Count - 1);

                tokens.Add(new Token(TokenKind.Other, ""));
                i = close;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;

                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;

                tokens.Add(new Token(TokenKind.Word, sql[start..i]));
                continue;
            }

            tokens.Add(c switch
            {
                '(' => new Token(TokenKind.Open, "("),
                ')' => new Token(TokenKind.Close, ")"),
                ',' => new Token(TokenKind.Comma, ","),
                '.' => new Token(TokenKind.Dot, "."),
                _ => new Token(TokenKind.Other, c.ToString()),
            });

            i++;
        }

        return tokens;
    }
}
