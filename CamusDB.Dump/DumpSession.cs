
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
    /// <summary>
    /// Names Windows resolves to a device rather than to a file, whatever extension follows them. A
    /// database called <c>NUL</c> would otherwise be dumped to <c>NUL.sql</c>, which discards every byte.
    /// </summary>
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly Options opts;

    /// <summary>Written at the end to standard error, so warnings survive a dump sent to standard output.</summary>
    private readonly List<(string Database, DumpWarnings Warnings)> collected = [];

    /// <summary>Guards the shared-directory warning, which belongs in the output once per run.</summary>
    private bool directoryWarned;

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
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("camus-dump: cancelled.").ConfigureAwait(false);
            return 1;
        }
        // Everything else — a gRPC RpcException, an IOException on the output file, an
        // UnauthorizedAccessException on the output directory — used to escape as a stack trace, which
        // in a cron pipeline buries the real failure and skips the clean exit code below. The type name
        // is kept in the message so a genuine defect is still recognisable.
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"camus-dump: {exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
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
            : OpenDumpFile(opts.Output);

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
            || database.Contains(Path.AltDirectorySeparatorChar)
            || WindowsDeviceNames.Contains(database)
            || database.EndsWith('.')
            || database.EndsWith(' '))
        {
            throw new DumpException(
                $"database '{database}' cannot be written under --output-directory: its name is not a usable file name. " +
                "Exclude it with --exclude-database, or dump it on its own with --database and --output.");
        }

        CreateOutputDirectory(opts.OutputDirectory!);

        return OpenDumpFile(Path.Combine(opts.OutputDirectory!, database + ".sql"));
    }

    /// <summary>
    /// Creates <c>--output-directory</c> so that only its owner can list or read it, and warns when a
    /// directory that already exists is writable by anybody else. That is the condition the file guards
    /// in <see cref="OpenDumpFile"/> exist for: another local user who can write into the directory can
    /// place a name there before the dump does.
    /// </summary>
    private void CreateOutputDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        if (directoryWarned)
            return;

        UnixFileMode mode = File.GetUnixFileMode(path);

        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
            return;

        directoryWarned = true;

        Console.Error.WriteLine(
            $"camus-dump: warning: '{path}' is writable by other users. A dump holds every row of the " +
            "database. Write it to a directory only its owner can write to.");
    }

    /// <summary>
    /// Opens a dump file for writing, with restrictive permissions and no symbolic link followed.
    ///
    /// <para>A dump holds the whole database, so the file is created readable and writable by its owner
    /// only, rather than by whatever the process umask allows. A file that already exists keeps its own
    /// mode through a truncation, so the mode is applied a second time on the open handle.</para>
    ///
    /// <para>A symbolic link is refused rather than followed. A dump job often runs as a privileged user
    /// and writes into a directory shared with others; a link placed at the name the dump is about to use
    /// would otherwise be followed, and the file at the far end truncated and overwritten.</para>
    /// </summary>
    private static TextWriter OpenDumpFile(string path)
    {
        RefuseSymbolicLink(path);

        FileStreamOptions options = new()
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        FileStream stream = new(path, options);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void RefuseSymbolicLink(string path)
    {
        FileSystemInfo? target;

        try
        {
            target = File.ResolveLinkTarget(path, returnFinalTarget: false);
        }
        catch (FileNotFoundException)
        {
            // Nothing is there yet, so the dump creates the file itself.
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if (target is null)
            return;

        throw new DumpException(
            $"'{path}' is a symbolic link to '{target.FullName}'. It is refused rather than followed, " +
            "because a dump would truncate and overwrite whatever is at the far end. " +
            "Write to a real path, or leave --output out to write the dump to standard output.");
    }

    private void ReportWarnings()
    {
        bool several = collected.Count > 1;

        foreach ((string database, DumpWarnings warnings) in collected)
            warnings.Report(Console.Error, several ? database : null);
    }
}
