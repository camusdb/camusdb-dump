
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Globalization;
using System.Text;
using CamusDB.Client;

namespace CamusDB.Dump;

/// <summary>
/// Builds the connection the dump runs over. Individual options (endpoint, database, credentials,
/// protocol) are folded into the connection string without overriding anything <c>--connection-source</c>
/// already set, so either style — one connection string, or a handful of flags — works on its own.
/// </summary>
internal static class ConnectionFactory
{
    private const string DefaultEndpoint = "http://localhost:5096";

    private const string DefaultDatabase = "test";

    /// <summary>
    /// The protocol used when neither <c>--protocol</c> nor the connection string picks one. It has to
    /// be written into the connection string explicitly: the driver's own default for an absent
    /// <c>Protocol=</c> key is REST, which against the gRPC port of <see cref="DefaultEndpoint"/> fails
    /// as "an HTTP/1.x request was sent to an HTTP/2 only endpoint".
    /// </summary>
    private const string DefaultProtocol = "grpc";

    /// <summary>Environment variable read when no password is given on the command line.</summary>
    public const string PasswordVariable = "CAMUSDB_PASSWORD";

    public static async Task<CamusConnection> CreateAsync(Options opts, CancellationToken cancellationToken = default)
    {
        SessionPoolOptions poolOptions = new()
        {
            MinimumPooledSessions = 1,
            MaximumActiveSessions = 20,
        };

        CamusConnectionStringBuilder builder = new(BuildConnectionString(opts))
        {
            SessionPoolManager = SessionPoolManager.Create(poolOptions)
        };

        CamusConnection connection = new(builder);

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using CamusPingCommand ping = connection.CreatePingCommand();
        await ping.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    public static string BuildConnectionString(Options opts)
    {
        Dictionary<string, string> settings = Parse(opts.ConnectionSource);

        // Credentials given as flags win over the ones the connection string carries: a caller who
        // passes --user is asking to connect as that user, and silently keeping the embedded one would
        // authenticate as somebody else.
        if (!string.IsNullOrEmpty(opts.User))
        {
            foreach (string alias in new[] { "User", "UserId", "Uid", "Username" })
                settings.Remove(alias);

            settings["User"] = opts.User;
        }

        string? password = ResolvePassword(opts);

        if (password is not null)
        {
            settings.Remove("Pwd");
            settings["Password"] = password;
        }

        if (!string.IsNullOrEmpty(opts.AccessToken))
            settings["AccessToken"] = opts.AccessToken;

        SetIfAbsent(settings, "Endpoint", opts.Endpoint ?? DefaultEndpoint);
        SetIfAbsent(settings, "Database", opts.Database ?? DefaultDatabase);

        if (!string.IsNullOrEmpty(opts.Protocol))
            settings["Protocol"] = opts.Protocol;
        else
            SetIfAbsent(settings, "Protocol", DefaultProtocol);

        if (opts.Timeout is int timeout)
            settings["Timeout"] = timeout.ToString(CultureInfo.InvariantCulture);

        if (opts.TokenLifetime is int lifetime)
            settings["TokenLifetime"] = lifetime.ToString(CultureInfo.InvariantCulture);

        StringBuilder sb = new();

        foreach (KeyValuePair<string, string> setting in settings)
        {
            if (sb.Length > 0)
                sb.Append(';');

            sb.Append(setting.Key).Append('=').Append(setting.Value);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The password to authenticate with, from <c>--password</c>, the <c>CAMUSDB_PASSWORD</c>
    /// environment variable, or a terminal prompt — in that order. Returns <see langword="null"/> when
    /// none applies, which leaves whatever the connection string carries (usually nothing, since
    /// CamusDB authentication is off by default) untouched.
    /// </summary>
    private static string? ResolvePassword(Options opts)
    {
        if (!string.IsNullOrEmpty(opts.Password))
            return opts.Password;

        string? fromEnvironment = Environment.GetEnvironmentVariable(PasswordVariable);

        if (!string.IsNullOrEmpty(fromEnvironment))
            return fromEnvironment;

        if (!opts.AskPassword)
            return null;

        return ReadPasswordFromTerminal();
    }

    private static string ReadPasswordFromTerminal()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";

        Console.Error.Write("Password: ");

        StringBuilder password = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                    password.Length--;

                continue;
            }

            if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }

        Console.Error.WriteLine();

        return password.ToString();
    }

    /// <summary>
    /// Splits a connection string into its settings, matching keys case-insensitively the way the
    /// driver's own builder does, and keeping the first occurrence of a repeated key.
    /// </summary>
    private static Dictionary<string, string> Parse(string? connectionString)
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(connectionString))
            return settings;

        foreach (string setting in connectionString.Split(';'))
        {
            string[] parts = setting.Split('=', 2);

            if (parts.Length != 2)
                continue;

            settings.TryAdd(parts[0].Trim(), parts[1]);
        }

        return settings;
    }

    private static void SetIfAbsent(Dictionary<string, string> settings, string key, string value)
    {
        if (!settings.ContainsKey(key))
            settings[key] = value;
    }

    /// <summary>The endpoint and database a header line can report, without leaking the credentials.</summary>
    public static (string Endpoint, string Database, string Protocol) Describe(Options opts)
    {
        Dictionary<string, string> settings = Parse(BuildConnectionString(opts));

        return (
            settings.TryGetValue("Endpoint", out string? endpoint) ? endpoint : DefaultEndpoint,
            settings.TryGetValue("Database", out string? database) ? database : DefaultDatabase,
            settings.TryGetValue("Protocol", out string? protocol) ? protocol.ToLowerInvariant() : DefaultProtocol);
    }
}
