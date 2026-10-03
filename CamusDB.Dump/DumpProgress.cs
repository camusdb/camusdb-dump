
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Globalization;
using Spectre.Console;

namespace CamusDB.Dump;

/// <summary>
/// Shows how far a dump has got: one progress bar per database, advanced a table at a time, with the
/// table being read and the rows written so far beside it.
///
/// <para>The bars are drawn on standard error, and only when the dump goes to a file: standard output
/// then carries nothing, and standard error is still the terminal the operator watches. A dump written
/// to standard output, or a run whose standard error is redirected (a cron job, a log file), gets
/// <see cref="None"/>, which draws nothing.</para>
///
/// <para>A table's size is not known before it is read — counting it would cost a second scan — so a
/// bar measures tables, not rows. The row count is shown as text.</para>
/// </summary>
internal sealed class DumpProgress
{
    /// <summary>Accepts every call and draws nothing.</summary>
    public static readonly DumpProgress None = new(null);

    private readonly ProgressContext? context;

    /// <summary>The bar of the database being dumped, or null before the first one starts.</summary>
    private ProgressTask? task;

    private string database = "";

    private string? table;

    private int tableCount;

    /// <summary>The rows written for the current table.</summary>
    private long tableRows;

    /// <summary>The rows written for the current database, over every table.</summary>
    private long databaseRows;

    private DumpProgress(ProgressContext? context)
    {
        this.context = context;
    }

    /// <summary>
    /// Whether the bars are drawn for this run: the dump goes to a file, standard error is a terminal, and
    /// <c>--no-progress</c> is not given.
    /// </summary>
    public static bool IsWanted(Options opts)
        => !opts.NoProgress
           && (!string.IsNullOrEmpty(opts.Output) || !string.IsNullOrEmpty(opts.OutputDirectory))
           && !Console.IsErrorRedirected;

    /// <summary>
    /// Draws the bars on standard error while <paramref name="dump"/> runs. The last state of each bar
    /// stays on the screen after it ends, so a failure shows which table it stopped at.
    /// </summary>
    public static Task RunAsync(Func<DumpProgress, Task> dump)
    {
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = TerminalSpeaksAnsi() ? AnsiSupport.Yes : AnsiSupport.Detect,
            Out = new AnsiConsoleOutput(Console.Error),
        });

        return console.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new SpinnerColumn(),
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new ElapsedTimeColumn())
            .StartAsync(context => dump(new DumpProgress(context)));
    }

    /// <summary>
    /// Whether a Unix terminal takes ANSI escapes. Spectre.Console decides that from a fixed list of
    /// <c>TERM</c> values, and a newer terminal missing from it (<c>TERM=rio</c>, for one) falls back to
    /// printing a line per update instead of redrawing the bars. Every terminal but <c>dumb</c> takes
    /// the few escapes the bars need; Windows is left to Spectre.Console, which asks the console itself.
    /// </summary>
    private static bool TerminalSpeaksAnsi()
    {
        if (OperatingSystem.IsWindows())
            return false;

        string? term = Environment.GetEnvironmentVariable("TERM");

        return !string.IsNullOrEmpty(term) && term != "dumb";
    }

    /// <summary>
    /// Adds the bar for a database. It spins without a fill until <see cref="TablesResolved"/> says how
    /// many tables it holds, because the connection and the table list come first.
    /// </summary>
    public void DatabaseStarted(string database)
    {
        if (context is null)
            return;

        this.database = database;
        table = null;
        tableCount = 0;
        tableRows = 0;
        databaseRows = 0;

        task = context.AddTask(Describe("connecting"));
        task.IsIndeterminate = true;
    }

    public void TablesResolved(int count)
    {
        if (task is null)
            return;

        tableCount = count;

        // A database with no tables still gets a bar that can fill, so it ends at 100% like the others.
        task.MaxValue = Math.Max(1, count);
        task.IsIndeterminate = false;
        task.Description = Describe(Tables(count));
    }

    public void TableStarted(string table)
    {
        if (task is null)
            return;

        this.table = table;
        tableRows = 0;

        task.Description = DescribeTable();
    }

    public void RowsWritten(int count)
    {
        if (task is null)
            return;

        tableRows += count;
        databaseRows += count;

        task.Description = DescribeTable();
    }

    public void TableCompleted()
    {
        task?.Increment(1);
    }

    public void DatabaseCompleted()
    {
        if (task is null)
            return;

        task.Value = task.MaxValue;
        task.Description = Describe($"{Tables(tableCount)}, {Rows(databaseRows)}");
        task.StopTask();
    }

    private string DescribeTable()
        => Describe($"{Markup.Escape(table ?? "")} [grey]({Rows(tableRows)})[/]");

    /// <summary>
    /// The text beside a bar. The database and table names come from the server, and a <c>[</c> in one
    /// is markup to Spectre.Console, so they are escaped; <paramref name="detail"/> is already markup.
    /// </summary>
    private string Describe(string detail)
        => $"[bold]{Markup.Escape(database)}[/] {detail}";

    private static string Tables(int count)
        => count == 1 ? "1 table" : count.ToString("N0", CultureInfo.InvariantCulture) + " tables";

    private static string Rows(long count)
        => count == 1 ? "1 row" : count.ToString("N0", CultureInfo.InvariantCulture) + " rows";
}
