
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using CamusDB.Client;

namespace CamusDB.Dump;

/// <summary>
/// Works out which databases the run covers: the single one the connection options name, or — with
/// <c>--all-databases</c> — everything <c>SHOW DATABASES</c> reports, minus <c>--exclude-database</c>.
/// </summary>
internal static class DatabaseCatalog
{
    public static async Task<List<string>> ResolveAsync(Options opts, CancellationToken cancellationToken = default)
    {
        HashSet<string> excluded = new(
            opts.ExcludeDatabases.Select(database => database.Trim()).Where(database => database.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        if (!opts.AllDatabases)
        {
            if (excluded.Count > 0)
                throw new DumpException("--exclude-database only means something together with --all-databases.");

            return [ConnectionFactory.Describe(opts).Database];
        }

        List<string> databases = await FetchAsync(opts, cancellationToken).ConfigureAwait(false);

        List<string> selected = databases.Where(database => !excluded.Contains(database)).ToList();

        if (selected.Count == 0)
            throw new DumpException(excluded.Count > 0
                ? "--exclude-database left no database to dump."
                : "the server reports no databases to dump.");

        return selected;
    }

    /// <summary>
    /// Lists the server's databases. <c>SHOW DATABASES</c> is answered server-wide rather than out of the
    /// connection's own database, so the catalog connection works even when the database the options name
    /// does not exist.
    /// </summary>
    private static async Task<List<string>> FetchAsync(Options opts, CancellationToken cancellationToken)
    {
        await using CamusConnection connection = await ConnectionFactory.CreateAsync(opts, cancellationToken: cancellationToken).ConfigureAwait(false);

        using CamusCommand cmd = connection.CreateSelectCommand("SHOW DATABASES");
        using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<string> databases = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            databases.Add(reader.GetString(reader.GetOrdinal("Database")));

        return databases;
    }
}
