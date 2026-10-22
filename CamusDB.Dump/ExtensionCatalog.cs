/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Globalization;
using CamusDB.Client;

namespace CamusDB.Dump;

/// <summary>One C# extension installed in the dumped database: its name, its installed version, and the extensions it requires.</summary>
internal sealed record ExtensionInfo(string Name, string Version, IReadOnlyList<string> Requires);

/// <summary>
/// Reads the C# extensions installed in a database and writes the SQL that installs them again.
///
/// <para>The dump writes <c>CREATE EXTENSION name VERSION 'v'</c>, never the members: the code lives in
/// the extension file, which the target server must load before the dump runs. The statements come
/// before the first table, because a column default, a CHECK constraint or a view can call an extension
/// function, and they come in dependency order, because the server refuses an extension whose required
/// extensions are not installed yet.</para>
///
/// <para>The list comes from <c>SHOW EXTENSIONS</c>: the rows whose status is <c>installed</c>, and the
/// rows whose status is <c>missing</c> (installed in the database, not loaded on the node that answered).
/// The statement needs a superuser; a dump taken by another user carries no extensions.</para>
/// </summary>
internal static class ExtensionCatalog
{
    /// <summary>
    /// Lists the extensions installed in the database, in an order that installs each one after the ones
    /// it requires, or returns null when the server refuses <c>SHOW EXTENSIONS</c> to this user because it
    /// is not a superuser. Any other error from the server ends the dump.
    /// </summary>
    public static async Task<List<ExtensionInfo>?> ReadAsync(Func<string, CamusCommand> createCommand, CancellationToken cancellationToken)
    {
        List<ExtensionInfo> installed = [];

        try
        {
            using CamusCommand cmd = createCommand("SHOW EXTENSIONS");
            using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string? status = Text(reader, "status");

                // A server older than the packaging layer has no status column and no installed extensions.
                if (status is not ("installed" or "missing"))
                    continue;

                string? requires = Text(reader, "requires");

                installed.Add(new ExtensionInfo(
                    Text(reader, "name") ?? "",
                    Text(reader, "version") ?? "",
                    string.IsNullOrEmpty(requires) ? [] : requires.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
        }
        // Only a refused privilege means the database may have extensions the dump cannot see. A server
        // older than C# extensions reads SHOW EXTENSIONS as a misspelled SHOW VARIABLES (CADB0400) or as
        // a syntax error (CADB0406), and such a server has no extensions to install.
        catch (CamusException exception) when (exception.Code == "CADB0517")
        {
            return null;
        }
        catch (CamusException exception) when (exception.Code is "CADB0400" or "CADB0406")
        {
            return [];
        }

        return InDependencyOrder(installed);
    }

    /// <summary>
    /// Orders the extensions so each comes after the installed extensions it requires. A requirement that
    /// is not in the list (the server refuses that state) does not hold an extension back.
    /// </summary>
    public static List<ExtensionInfo> InDependencyOrder(List<ExtensionInfo> extensions)
    {
        HashSet<string> names = new(extensions.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        HashSet<string> placed = new(StringComparer.OrdinalIgnoreCase);
        List<ExtensionInfo> ordered = new(extensions.Count);
        List<ExtensionInfo> pending = extensions.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

        while (pending.Count > 0)
        {
            ExtensionInfo? next = pending.FirstOrDefault(e => e.Requires.All(r => placed.Contains(r) || !names.Contains(r)));

            // A cycle cannot be installed in the first place; keep the rest in name order.
            next ??= pending[0];

            ordered.Add(next);
            placed.Add(next.Name);
            pending.Remove(next);
        }

        return ordered;
    }

    /// <summary>The <c>CREATE EXTENSION</c> that installs <paramref name="extension"/> at its installed version.</summary>
    public static string CreateStatement(ExtensionInfo extension) =>
        "CREATE EXTENSION " + SqlLiteral.Identifier(extension.Name, "extension") + " VERSION " + SqlLiteral.Quote(extension.Version) + ";";

    /// <summary>
    /// The <c>DROP EXTENSION IF EXISTS</c> that <c>--add-drop-table</c> writes, after the tables are dropped.
    /// The IF EXISTS form lets the dump load into a database that does not have the extension; a server
    /// older than that form refuses it as a syntax error.
    /// </summary>
    public static string DropStatement(ExtensionInfo extension) =>
        "DROP EXTENSION IF EXISTS " + SqlLiteral.Identifier(extension.Name, "extension") + ";";

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
