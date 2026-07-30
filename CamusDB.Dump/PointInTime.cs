
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Globalization;
using System.Text.RegularExpressions;

namespace CamusDB.Dump;

/// <summary>
/// The instant the dump reads at, expressed as CamusDB's <c>AS OF SYSTEM TIME</c> clause.
///
/// <para>A dump that reads the latest data is only as consistent as the moment each table happened to be
/// scanned: a row written between the first table and the last one lands in the dump without whatever it
/// referenced in an earlier table. Reading every table at one fixed past instant removes that, which is
/// why it is the default here — the alternative is <c>--single-transaction</c>, or <c>--no-as-of</c> to
/// take the latest data with no consistency guarantee at all.</para>
///
/// <para>Relative offsets are resolved to an absolute instant once, when the dump starts, rather than
/// being passed through as <c>'-10s'</c>. The server evaluates a relative offset against its clock at the
/// time the statement arrives, so passing one through would give each table a slightly different snapshot
/// and defeat the point of asking for a fixed one.</para>
/// </summary>
internal sealed class PointInTime
{
    /// <summary>
    /// How far behind the wall clock the default instant sits. The server rejects a snapshot that
    /// resolves to a future physical millisecond by its own clock, so "now" as this machine sees it is
    /// not safe to ask for: a second of tolerance covers ordinary clock skew between the two hosts.
    /// </summary>
    private static readonly TimeSpan DefaultLag = TimeSpan.FromSeconds(1);

    /// <summary>Matches the relative forms the server accepts — <c>-10s</c>, <c>-500ms</c>, <c>-1h</c>.</summary>
    private static readonly Regex RelativePattern =
        new(@"^-\s*(\d+)\s*(ms|s|m|h|d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The instant every table is read at.</summary>
    public DateTimeOffset Instant { get; }

    /// <summary>What the user asked for — <c>-10s</c>, an absolute timestamp — or null when defaulted.</summary>
    public string? Requested { get; }

    private PointInTime(DateTimeOffset instant, string? requested)
    {
        Instant = instant;
        Requested = requested;
    }

    /// <summary>
    /// The clause appended to a <c>SELECT</c>, right after the table and before any <c>WHERE</c>.
    /// The instant is rendered to millisecond precision, which is what the snapshot resolves to.
    /// </summary>
    public string Clause => $"AS OF SYSTEM TIME '{Timestamp}'";

    public string Timestamp => Instant.ToString("yyyy-MM-dd HH:mm:ss.fff'+00:00'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The instant to dump at, or <see langword="null"/> when the dump should read the latest data —
    /// either because <c>--no-as-of</c> asked for it, or because <c>--single-transaction</c> already
    /// pins every table to one snapshot and the server rejects the clause inside a transaction.
    /// </summary>
    public static PointInTime? Resolve(Options opts)
    {
        bool requested = !string.IsNullOrWhiteSpace(opts.AsOf);

        if (opts.SingleTransaction)
        {
            if (requested)
                throw new DumpException(
                    "--as-of cannot be combined with --single-transaction: CamusDB rejects AS OF SYSTEM TIME " +
                    "inside an explicit transaction, which already reads every table from one snapshot.");

            return null;
        }

        if (opts.NoAsOf)
        {
            if (requested)
                throw new DumpException("--as-of and --no-as-of contradict each other.");

            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!requested)
            return new PointInTime(now - DefaultLag, null);

        string value = opts.AsOf!.Trim();
        DateTimeOffset instant = Parse(value, now);

        if (instant >= now)
            throw new DumpException($"--as-of {value} resolves to {Render(instant)}, which is not in the past.");

        if (instant <= DateTimeOffset.UnixEpoch)
            throw new DumpException($"--as-of {value} resolves to {Render(instant)}, at or before the Unix epoch.");

        return new PointInTime(instant, value);
    }

    /// <summary>
    /// Reads the three forms the server's own resolver accepts: a negative duration offset, Unix epoch
    /// milliseconds, or an absolute timestamp (assumed UTC when it carries no offset).
    /// </summary>
    private static DateTimeOffset Parse(string value, DateTimeOffset now)
    {
        Match relative = RelativePattern.Match(value);

        if (relative.Success)
        {
            if (!long.TryParse(relative.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long magnitude))
                throw new DumpException($"--as-of {value} is out of range.");

            try
            {
                TimeSpan offset = relative.Groups[2].Value.ToLowerInvariant() switch
                {
                    "ms" => TimeSpan.FromMilliseconds(magnitude),
                    "s" => TimeSpan.FromSeconds(magnitude),
                    "m" => TimeSpan.FromMinutes(magnitude),
                    "h" => TimeSpan.FromHours(magnitude),
                    _ => TimeSpan.FromDays(magnitude),
                };

                return now - offset;
            }
            catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
            {
                throw new DumpException($"--as-of {value} is too far in the past to represent.");
            }
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epochMilliseconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new DumpException($"--as-of {value} is not a representable Unix epoch millisecond value.");
            }
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset absolute))
        {
            return absolute;
        }

        throw new DumpException(
            $"Invalid --as-of value '{value}'. Expected a negative offset such as -10s, an absolute " +
            "timestamp such as '2026-07-19 20:00:00+00:00', or Unix epoch milliseconds.");
    }

    private static string Render(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff'+00:00'", CultureInfo.InvariantCulture);
}
