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
/// One row trigger of a dumped table: its name, its table, its form (<c>csharp</c> for an inline body,
/// <c>extension</c> for a type of an installed extension), its owner, and the statement that creates
/// it again, as the server renders it.
/// </summary>
internal sealed record TriggerInfo(string Name, string Table, string Form, string? Owner, string CreateStatement);

/// <summary>
/// Reads the row triggers of the dumped tables and writes the SQL that creates them again.
///
/// <para><b>Both forms.</b> The statement comes from <c>SHOW CREATE TRIGGER</c>: the DLL form names its
/// type (<c>EXTERNAL NAME '...'</c>), so the target needs the same extension, which the dump installs;
/// the inline form carries its whole C# body between dollar quotes.</para>
///
/// <para><b>After the rows.</b> The statements come after the last table and its rows: a trigger that
/// exists while the rows load would fire for each one, change them or write rows of its own a second
/// time. A trigger's SQL can also write a table that comes later in the dump.</para>
///
/// <para><b>Owners.</b> The SQL of an inline trigger runs with its owner's rights, and the owner is the
/// user who creates it. The user who loads the dump therefore owns every inline trigger it creates;
/// there is no statement that gives a trigger to another user. The dump says so when a trigger had
/// another owner.</para>
///
/// <para>The list comes from <c>SHOW TRIGGERS</c>, which needs <c>SELECT</c> or <c>ALTER</c> on the
/// database. The target server must accept the inline form (<c>inline_triggers_enabled</c>).</para>
/// </summary>
internal static class TriggerCatalog
{
    /// <summary>
    /// Lists the triggers of <paramref name="tables"/> in the order of the tables and then in creation
    /// order, which is the order the triggers of one timing fire in, or returns null when the server
    /// refuses <c>SHOW TRIGGERS</c> to this user. A server older than <c>SHOW TRIGGERS</c> returns an
    /// empty list. Any other error from the server ends the dump.
    /// </summary>
    public static async Task<List<TriggerInfo>?> ReadAsync(
        Func<string, CamusCommand> createCommand, IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        List<(string Name, string Table, string Form, string? Owner)> listed = [];

        try
        {
            using CamusCommand cmd = createCommand("SHOW TRIGGERS");
            using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Text(reader, "name") is { Length: > 0 } name && Text(reader, "table") is { Length: > 0 } table)
                    listed.Add((name, table, Text(reader, "form") ?? "", Text(reader, "owner")));
            }
        }
        catch (CamusException exception) when (exception.Code == "CADB0517")
        {
            return null;
        }
        // A server older than SHOW TRIGGERS reads it as a misspelled SHOW VARIABLES (CADB0400) or as a
        // syntax error (CADB0406). Such a server cannot list its triggers.
        catch (CamusException exception) when (exception.Code is "CADB0400" or "CADB0406")
        {
            return [];
        }

        Dictionary<string, int> order = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < tables.Count; i++)
            order[tables[i]] = i;

        List<TriggerInfo> triggers = new(listed.Count);

        // A stable sort keeps the server's creation order within one table.
        foreach ((string name, string table, string form, string? owner) in listed
                     .Where(t => order.ContainsKey(t.Table))
                     .OrderBy(t => order[t.Table]))
        {
            using CamusCommand cmd = createCommand(
                "SHOW CREATE TRIGGER " + SqlLiteral.Identifier(name, "trigger") + " ON " + SqlLiteral.Identifier(table, "table"));
            using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || Text(reader, "create trigger") is not { Length: > 0 } statement)
                throw new DumpException($"SHOW CREATE TRIGGER {name} ON {table} returned no statement.");

            triggers.Add(new TriggerInfo(name, table, form, owner, statement));
        }

        return triggers;
    }

    /// <summary>The server's own <c>CREATE TRIGGER</c> for <paramref name="trigger"/>, ended with a semicolon.</summary>
    public static string CreateStatement(TriggerInfo trigger) =>
        trigger.CreateStatement.TrimEnd().TrimEnd(';') + ";";

    /// <summary>True for an inline trigger whose owner is not <paramref name="dumpUser"/>: on the target its SQL runs with other rights.</summary>
    public static bool ChangesOwner(TriggerInfo trigger, string? dumpUser) =>
        string.Equals(trigger.Form, "csharp", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(trigger.Owner)
        && !string.Equals(trigger.Owner, dumpUser, StringComparison.OrdinalIgnoreCase);

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
