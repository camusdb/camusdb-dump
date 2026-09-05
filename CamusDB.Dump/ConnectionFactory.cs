
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
/// protocol) are folded into the driver's settings without overriding anything <c>--connection-source</c>
/// already set, so either style — one connection string, or a handful of flags — works on its own.
/// </summary>
internal static class ConnectionFactory
{
    private const string DefaultEndpoint = "http://localhost:5096";

    private const string DefaultDatabase = "test";

    /// <summary>
    /// The protocol used when neither <c>--protocol</c> nor the connection string picks one. It has to
    /// be written into the settings explicitly: the driver's own default for an absent <c>Protocol</c>
    /// key is REST, which against the gRPC port of <see cref="DefaultEndpoint"/> fails as "an HTTP/1.x
    /// request was sent to an HTTP/2 only endpoint".
    /// </summary>
    private const string DefaultProtocol = "grpc";

    /// <summary>Environment variable read when no password is given on the command line.</summary>
    public const string PasswordVariable = "CAMUSDB_PASSWORD";

    /// <summary>Environment variable read when no access token is given on the command line.</summary>
    public const string AccessTokenVariable = "CAMUSDB_ACCESS_TOKEN";

    /// <summary>The keys that name a user, in the order the driver reads them.</summary>
    private static readonly string[] UserAliases = ["User", "UserId", "Uid", "Username"];

    /// <summary>The keys that carry a secret, so a descriptive call can drop every one of them.</summary>
    private static readonly string[] SecretKeys = ["Password", "Pwd", "AccessToken"];

    /// <summary>
    /// Every key the driver reads, in the exact spelling it reads it by. Its settings dictionary
    /// compares keys case sensitively, so a key written as <c>endpoint=</c> in <c>--connection-source</c>
    /// reaches the driver but is never looked up, and the default silently applies instead. Each key is
    /// therefore mapped back to this spelling on the way in.
    /// </summary>
    private static readonly Dictionary<string, string> CanonicalKeys = new[]
    {
        "AccessToken", "AutoPrepareMinUsages", "BackupEndpoint", "BackupTimeout", "ChannelPoolSize",
        "CoalescingDelay", "CoalescingThreshold", "Database", "Endpoint", "IsolationLevel", "Locking",
        "MaxAutoPrepare", "Password", "Protocol", "Pwd", "Timeout", "TokenLifetime", "TransactionMode",
        "Uid", "User", "UserId", "Username",
    }.ToDictionary(key => key, key => key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The password <c>--ask-password</c> read from the terminal, kept so the prompt appears once. A
    /// connection is opened for every database, and a prompt at each of those points looks to the
    /// operator like the first password was refused.
    /// </summary>
    private static string? promptedPassword;

    /// <summary>Guards the plaintext-endpoint warning, which belongs in the output once per run.</summary>
    private static bool plaintextWarned;

    /// <summary>
    /// Opens a connection and proves it works with a ping. <paramref name="database"/> overrides the
    /// database the options resolve to, which is how <c>--all-databases</c> gets one connection per
    /// database — there is no <c>USE</c> statement to switch an open one over.
    /// </summary>
    public static async Task<CamusConnection> CreateAsync(Options opts, string? database = null, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> settings = BuildSettings(opts, database, withCredentials: true);

        WarnOnPlaintextEndpoint(settings);

        // Nothing is pooled per statement beyond the driver's own gRPC streams, sized by the
        // ChannelPoolSize connection-string key; its default of 2 suits a dump, which reads one table
        // at a time.
        CamusConnection connection = new(BuildBuilder(settings));

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using CamusPingCommand ping = connection.CreatePingCommand();
        await ping.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    /// <summary>
    /// Hands the settings to the driver one key at a time.
    ///
    /// <para>They are deliberately not serialized into a <c>key=value;key=value</c> string first. Such a
    /// string has to be split on <c>;</c> and <c>=</c> to be read back, and neither character can be
    /// escaped, so a value that contains one breaks the string apart. A password of
    /// <c>p@ss;Endpoint=http://elsewhere</c> used to add a second <c>Endpoint</c> key; the driver keeps
    /// the first occurrence of a key, so that injected endpoint won, and the credentials went to it.</para>
    /// </summary>
    private static CamusConnectionStringBuilder BuildBuilder(Dictionary<string, string> settings)
    {
        CamusConnectionStringBuilder builder = new("");

        foreach (KeyValuePair<string, string> setting in settings)
            builder.Config[setting.Key] = setting.Value;

        return builder;
    }

    /// <summary>
    /// The settings a connection is opened with. <paramref name="withCredentials"/> is false for the
    /// descriptive call behind the dump header: it drops every secret, and it never reaches the
    /// terminal prompt, so describing a connection cannot ask for a password.
    /// </summary>
    private static Dictionary<string, string> BuildSettings(Options opts, string? database, bool withCredentials)
    {
        Dictionary<string, string> settings = Parse(opts.ConnectionSource);

        if (withCredentials)
            ApplyCredentials(opts, settings);
        else
            RemoveCredentials(settings);

        SetIfAbsent(settings, "Endpoint", opts.Endpoint ?? DefaultEndpoint);

        // An explicitly named database is the one being dumped right now, so it replaces whatever the
        // connection string carries; without one, the connection string keeps precedence as usual.
        if (!string.IsNullOrEmpty(database))
            settings["Database"] = database;
        else
            SetIfAbsent(settings, "Database", opts.Database ?? DefaultDatabase);

        if (!string.IsNullOrEmpty(opts.Protocol))
            settings["Protocol"] = opts.Protocol;
        else
            SetIfAbsent(settings, "Protocol", DefaultProtocol);

        if (opts.Timeout is int timeout)
            settings["Timeout"] = timeout.ToString(CultureInfo.InvariantCulture);

        if (opts.TokenLifetime is int lifetime)
            settings["TokenLifetime"] = lifetime.ToString(CultureInfo.InvariantCulture);

        return settings;
    }

    /// <summary>
    /// Folds the credential options into the settings. Credentials given as flags win over the ones the
    /// connection string carries: a caller who passes <c>--user</c> is asking to connect as that user,
    /// and silently keeping the embedded one would authenticate as somebody else.
    /// </summary>
    private static void ApplyCredentials(Options opts, Dictionary<string, string> settings)
    {
        if (!string.IsNullOrEmpty(opts.User))
        {
            foreach (string alias in UserAliases)
                settings.Remove(alias);

            settings["User"] = opts.User;
        }

        string? password = ResolvePassword(opts);

        if (password is not null)
        {
            settings.Remove("Pwd");
            settings["Password"] = password;
        }

        string? accessToken = ResolveAccessToken(opts);

        if (accessToken is not null)
            settings["AccessToken"] = accessToken;
    }

    private static void RemoveCredentials(Dictionary<string, string> settings)
    {
        foreach (string key in SecretKeys)
            settings.Remove(key);
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

        return promptedPassword ??= ReadPasswordFromTerminal();
    }

    /// <summary>
    /// The bearer token to present, from <c>--access-token</c> or the <c>CAMUSDB_ACCESS_TOKEN</c>
    /// environment variable. The variable exists for the same reason the password has one: a token on
    /// the command line is a ready-made credential that every local user can read from the process list.
    /// </summary>
    private static string? ResolveAccessToken(Options opts)
    {
        if (!string.IsNullOrEmpty(opts.AccessToken))
            return opts.AccessToken;

        string? fromEnvironment = Environment.GetEnvironmentVariable(AccessTokenVariable);

        return string.IsNullOrEmpty(fromEnvironment) ? null : fromEnvironment;
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
    /// Warns once when the dump travels unencrypted to another host. The server refuses a credential
    /// exchange over plaintext outside loopback, but the rows themselves carry no such control, and a
    /// server with authentication off has none at all.
    /// </summary>
    private static void WarnOnPlaintextEndpoint(Dictionary<string, string> settings)
    {
        if (plaintextWarned || !settings.TryGetValue("Endpoint", out string? endpoint))
            return;

        foreach (string candidate in endpoint.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
                continue;

            if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.IsLoopback)
                continue;

            plaintextWarned = true;

            Console.Error.WriteLine(
                $"camus-dump: warning: {uri.Scheme}://{uri.Authority} is a plaintext endpoint. " +
                "The dumped rows travel unencrypted. Use an https:// endpoint where the server offers one.");

            return;
        }
    }

    /// <summary>
    /// Splits a connection string into its settings, keeping the first occurrence of a repeated key and
    /// mapping each key to the spelling the driver reads it by.
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

            settings.TryAdd(Canonical(parts[0].Trim()), parts[1]);
        }

        return settings;
    }

    private static string Canonical(string key)
        => CanonicalKeys.TryGetValue(key, out string? canonical) ? canonical : key;

    private static void SetIfAbsent(Dictionary<string, string> settings, string key, string value)
    {
        if (!settings.ContainsKey(key))
            settings[key] = value;
    }

    /// <summary>The endpoint and database a header line can report, without leaking the credentials.</summary>
    public static (string Endpoint, string Database, string Protocol) Describe(Options opts, string? database = null)
    {
        Dictionary<string, string> settings = BuildSettings(opts, database, withCredentials: false);

        return (
            StripUserInfo(settings.TryGetValue("Endpoint", out string? endpoint) ? endpoint : DefaultEndpoint),
            settings.TryGetValue("Database", out string? resolved) ? resolved : DefaultDatabase,
            settings.TryGetValue("Protocol", out string? protocol) ? protocol.ToLowerInvariant() : DefaultProtocol);
    }

    /// <summary>
    /// The endpoint with any <c>user:password@</c> part replaced. The header line is written into the
    /// dump file, and dump files are copied between environments and attached to tickets, so an endpoint
    /// that carries credentials must not travel with them.
    /// </summary>
    private static string StripUserInfo(string endpoint)
    {
        string[] candidates = endpoint.Split(',');

        for (int i = 0; i < candidates.Length; i++)
            candidates[i] = StripOneUserInfo(candidates[i]);

        return string.Join(",", candidates);
    }

    private static string StripOneUserInfo(string endpoint)
    {
        int authority = endpoint.IndexOf("//", StringComparison.Ordinal);

        if (authority < 0)
            return endpoint;

        int start = authority + 2;
        int at = endpoint.IndexOf('@', start);

        if (at < 0)
            return endpoint;

        // An '@' past the end of the authority belongs to a path or a query, not to a userinfo part.
        int end = endpoint.IndexOfAny(['/', '?', '#'], start);

        if (end >= 0 && end < at)
            return endpoint;

        return string.Concat(endpoint.AsSpan(0, start), "***@", endpoint.AsSpan(at + 1));
    }
}
