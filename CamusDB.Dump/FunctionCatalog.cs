/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Globalization;
using CamusDB.Client;

namespace CamusDB.Dump;

/// <summary>
/// One inline SQL function of the dumped database (<c>CREATE FUNCTION ... LANGUAGE csharp</c>): its
/// name, its owner, and the statement that creates it again, as the server renders it.
/// </summary>
internal sealed record FunctionInfo(string Name, string? Owner, string CreateStatement);

/// <summary>
/// Reads the inline functions of a database and writes the SQL that creates them again.
///
/// <para>The source of a function is in the database, so the dump carries it whole: the statement comes
/// from <c>SHOW CREATE FUNCTION</c>, with the body between dollar quotes exactly as the author wrote
/// it. The statements come after the extensions and before the first table, because a CHECK
/// constraint or a column default can call a function, and the server resolves the name when the
/// table is created.</para>
///
/// <para><b>Owners.</b> The user who loads the dump owns the functions it creates. When a function
/// had another owner, the dump writes <c>ALTER FUNCTION ... OWNER TO</c> after it; that statement
/// needs the user to exist on the target and the loading user to be a superuser.</para>
///
/// <para><b>The reader of the dump.</b> A dollar-quoted body holds semicolons. A client that cuts a
/// script into statements must read the text between two equal dollar quotes as one piece.</para>
///
/// <para>The list comes from <c>SHOW FUNCTIONS</c>, which needs <c>SELECT</c> or <c>CREATE</c> on the
/// database. The target server must accept <c>CREATE FUNCTION</c>
/// (<c>inline_functions_enabled</c>).</para>
/// </summary>
internal static class FunctionCatalog
{
    /// <summary>
    /// Lists the inline functions of the database in name order, or returns null when the server
    /// refuses <c>SHOW FUNCTIONS</c> to this user. A server older than inline functions has none. Any
    /// other error from the server ends the dump.
    /// </summary>
    public static async Task<List<FunctionInfo>?> ReadAsync(Func<string, CamusCommand> createCommand, CancellationToken cancellationToken)
    {
        List<(string Name, string? Owner)> listed = [];

        try
        {
            using CamusCommand cmd = createCommand("SHOW FUNCTIONS");
            using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // A row with no language is the code of a dropped function that the node still unloads.
                if (Text(reader, "language") is null)
                    continue;

                if (Text(reader, "name") is { Length: > 0 } name)
                    listed.Add((name, Text(reader, "owner")));
            }
        }
        catch (CamusException exception) when (exception.Code == "CADB0517")
        {
            return null;
        }
        // A server older than inline functions reads SHOW FUNCTIONS as a misspelled SHOW VARIABLES
        // (CADB0400) or as a syntax error (CADB0406), and such a server has no functions.
        catch (CamusException exception) when (exception.Code is "CADB0400" or "CADB0406")
        {
            return [];
        }

        List<FunctionInfo> functions = new(listed.Count);

        foreach ((string name, string? owner) in listed.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            using CamusCommand cmd = createCommand("SHOW CREATE FUNCTION " + SqlLiteral.Identifier(name, "function"));
            using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || Text(reader, "create function") is not { Length: > 0 } statement)
                throw new DumpException($"SHOW CREATE FUNCTION {name} returned no statement.");

            functions.Add(new FunctionInfo(name, owner, statement));
        }

        return functions;
    }

    /// <summary>
    /// The statements that create <paramref name="function"/> again: the server's own
    /// <c>CREATE FUNCTION</c>, and <c>ALTER FUNCTION ... OWNER TO</c> when the function has an owner
    /// that is not <paramref name="dumpUser"/>, the user the dump connected as.
    /// </summary>
    public static IEnumerable<string> CreateStatements(FunctionInfo function, string? dumpUser)
    {
        yield return function.CreateStatement.TrimEnd().TrimEnd(';') + ";";

        if (!string.IsNullOrEmpty(function.Owner) && !string.Equals(function.Owner, dumpUser, StringComparison.OrdinalIgnoreCase))
            yield return "ALTER FUNCTION " + SqlLiteral.Identifier(function.Name, "function") + " OWNER TO " + SqlLiteral.Identifier(function.Owner, "user") + ";";
    }

    /// <summary>
    /// The <c>DROP FUNCTION IF EXISTS</c> that <c>--add-drop-table</c> writes, after the tables are
    /// dropped: a table's CHECK constraint can call the function, and the server refuses to drop a
    /// function that is in use.
    /// </summary>
    public static string DropStatement(FunctionInfo function) =>
        "DROP FUNCTION IF EXISTS " + SqlLiteral.Identifier(function.Name, "function") + ";";

    private static string? Text(CamusDataReader reader, string field)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (!string.Equals(reader.GetName(i), field, StringComparison.OrdinalIgnoreCase))
                continue;

            if (reader.IsDBNull(i))
                return null;

            ColumnValue value = reader.GetColumnValue(i);

            return value.Type == ColumnType.Null ? null : value.StrValue ?? Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
        }

        return null;
    }
}
