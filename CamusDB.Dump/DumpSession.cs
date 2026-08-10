
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Text;
using CamusDB.Client;

namespace CamusDB.Dump;

/// <summary>
/// Runs the dump over every selected database, one connection each, and decides where the output goes:
/// a single stream (standard output or <c>--output</c>) that holds every database one after another, or
/// one file per database under <c>--output-directory</c>.
/// </summary>
internal sealed class DumpSession
{
    private readonly Options opts;

    /// <summary>Written at the end to standard error, so warnings survive a dump sent to standard output.</summary>
    private readonly List<(string Database, DumpWarnings Warnings)> collected = [];

    public DumpSession(Options opts)
    {
        this.opts = opts;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        // A single stream is opened once and shared by every database; one file per database is opened
        // inside the loop instead, and null here marks that mode.
        TextWriter? shared = null;

        try
        {
            if (!string.IsNullOrEmpty(opts.Output) && !string.IsNullOrEmpty(opts.OutputDirectory))
                throw new DumpException("--output and --output-directory cannot both be given.");

            // Fixed before the first statement goes out, so every table of every database is read at the
            // same instant rather than at whatever moment its own scan happened to start.
            PointInTime? pointInTime = PointInTime.Resolve(opts);

            List<string> databases = await DatabaseCatalog.ResolveAsync(opts, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(opts.OutputDirectory))
                shared = OpenSharedOutput();

            foreach (string database in databases)
            {
                DumpWarnings warnings = new(opts.Strict);
                collected.Add((database, warnings));

                TextWriter output = shared ?? OpenDatabaseFile(database);

                try
                {
                    await DumpDatabaseAsync(database, pointInTime, output, warnings, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);

                    if (!ReferenceEquals(output, shared))
                        await output.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (DumpException exception)
        {
            await Console.Error.WriteLineAsync("camus-dump: " + exception.Message).ConfigureAwait(false);
            return 1;
        }
        catch (CamusException exception)
        {
            await Console.Error.WriteLineAsync($"camus-dump: server error {exception.Code}: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (shared is not null)
            {
                await shared.FlushAsync(CancellationToken.None).ConfigureAwait(false);

                if (!ReferenceEquals(shared, Console.Out))
                    await shared.DisposeAsync().ConfigureAwait(false);
            }

            ReportWarnings();
        }

        return 0;
    }

    private async Task DumpDatabaseAsync(
        string database,
        PointInTime? pointInTime,
        TextWriter output,
        DumpWarnings warnings,
        CancellationToken cancellationToken)
    {
        await using CamusConnection connection = await ConnectionFactory.CreateAsync(opts, database, cancellationToken).ConfigureAwait(false);

        // A dump spanning several tables is only self-consistent if every table is read from the same
        // snapshot; a serializable read-only transaction gives that without taking locks against writers.
        // It covers one database: a transaction belongs to the connection, and each database has its own.
        CamusTransaction? transaction = opts.SingleTransaction
            ? await connection.BeginTransactionAsync(CamusTransactionOptions.Snapshot).ConfigureAwait(false)
            : null;

        try
        {
            Dumper dumper = new(connection, transaction, opts, database, pointInTime, output, warnings);

            await dumper.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (transaction is not null)
            {
                // The dump wrote nothing, so the snapshot is released rather than committed.
                await transaction.RollbackAsync().ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private TextWriter OpenSharedOutput()
        => string.IsNullOrEmpty(opts.Output)
            ? Console.Out
            : new StreamWriter(opts.Output, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>
    /// The <c>&lt;database&gt;.sql</c> file a database is dumped to under <c>--output-directory</c>. The name
    /// comes from the server, so it is checked against the file system's rules rather than trusted to be a
    /// usable — or contained — file name.
    /// </summary>
    private TextWriter OpenDatabaseFile(string database)
    {
        if (database.Length == 0
            || database is "." or ".."
            || database.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || database.Contains(Path.DirectorySeparatorChar)
            || database.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new DumpException(
                $"database '{database}' cannot be written under --output-directory: its name is not a usable file name. " +
                "Exclude it with --exclude-database, or dump it on its own with --database and --output.");
        }

        Directory.CreateDirectory(opts.OutputDirectory!);

        string path = Path.Combine(opts.OutputDirectory!, database + ".sql");

        return new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void ReportWarnings()
    {
        bool several = collected.Count > 1;

        foreach ((string database, DumpWarnings warnings) in collected)
            warnings.Report(Console.Error, several ? database : null);
    }
}
