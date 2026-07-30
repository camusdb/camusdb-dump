
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Text;
using CamusDB.Client;
using CamusDB.Dump;
using CommandLine;

ParserResult<Options> optsResult = Parser.Default.ParseArguments<Options>(args);

Options? opts = optsResult.Value;
if (opts is null)
    return 1;

DumpWarnings warnings = new(opts.Strict);

TextWriter output = string.IsNullOrEmpty(opts.Output)
    ? Console.Out
    : new StreamWriter(opts.Output, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

try
{
    // Fixed before the first statement goes out, so every table is read at the same instant rather than
    // at whatever moment its own scan happened to start.
    PointInTime? pointInTime = PointInTime.Resolve(opts);

    await using CamusConnection connection = await ConnectionFactory.CreateAsync(opts);

    // A dump spanning several tables is only self-consistent if every table is read from the same
    // snapshot; a serializable read-only transaction gives that without taking locks against writers.
    CamusTransaction? transaction = opts.SingleTransaction
        ? await connection.BeginTransactionAsync(CamusTransactionOptions.Snapshot)
        : null;

    try
    {
        Dumper dumper = new(connection, transaction, opts, pointInTime, output, warnings);

        await dumper.RunAsync();
    }
    finally
    {
        if (transaction is not null)
        {
            // The dump wrote nothing, so the snapshot is released rather than committed.
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
        }
    }
}
catch (DumpException exception)
{
    await output.FlushAsync();
    await Console.Error.WriteLineAsync("camus-dump: " + exception.Message);
    return 1;
}
catch (CamusException exception)
{
    await output.FlushAsync();
    await Console.Error.WriteLineAsync($"camus-dump: server error {exception.Code}: {exception.Message}");
    return 1;
}
finally
{
    await output.FlushAsync();

    if (!ReferenceEquals(output, Console.Out))
        await output.DisposeAsync();
}

warnings.Report(Console.Error);

return 0;
