
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using CamusDB.Client;

namespace CamusDB.Dump;

/// <summary>One grant, as <c>SHOW GRANTS</c> reports it.</summary>
/// <param name="User">The account the grant belongs to.</param>
/// <param name="Object">The object it covers: <c>*.*</c>, <c>db.*</c> or <c>db.table</c>.</param>
/// <param name="Privileges">The privilege names, comma-separated, or <c>ALL PRIVILEGES</c>.</param>
internal sealed record UserGrant(string User, string Object, string Privileges);

/// <summary>One rendered <c>GRANT</c> statement, and the account it belongs to.</summary>
/// <param name="User">The account, so that the statements of one account can be kept together.</param>
/// <param name="Statement">The statement, rendered and checked when the grant was read.</param>
internal sealed record GrantStatement(string User, string Statement);

/// <summary>
/// Exports database accounts and their grants, which a dump otherwise leaves behind.
///
/// <para>A storage revision upgrade empties the user catalog, so after the reimport the server holds
/// the bootstrap superuser and nothing else. Every other account, and every grant ever issued by hand,
/// is gone with no record of what it was. That was the most expensive part of the migration this class
/// was written for: 24 accounts had to be rebuilt from a platform's own control tables, and the grants
/// from memory.</para>
///
/// <para><b>Three limits are built in, and none of them is a defect of this class.</b></para>
///
/// <list type="number">
///   <item><b>No password is exported.</b> The catalog keeps a salted PBKDF2 verifier, never the
///     password, and <c>CREATE USER</c> accepts cleartext only, so there is no statement that restores a
///     login unchanged. Each account is created without a password. The server refuses every login to an
///     account with no password, so the restored account is closed until an operator sets one. That is
///     also what keeps the dump file no more sensitive than it already is.</item>
///   <item><b>Superuser status is not exported.</b> Only the bootstrap user grants it, at server start,
///     and <c>SHOW GRANTS</c> does not report it, so an account that was a superuser comes back as an
///     ordinary account.</item>
///   <item><b>Only a current server can list the accounts.</b> <c>--all-users</c> reads them with
///     <c>SHOW USERS</c> and <c>SHOW GRANTS FOR *</c>. A server older than those statements answers only
///     <c>SHOW GRANTS FOR name</c>, one account at a time, so <c>--users</c> takes the names instead —
///     and that is the server a migration dump is usually taken from. Naming an account that does not
///     exist fails the dump rather than passing over it, because an account silently missing from the
///     dump is the failure these options exist to prevent.</item>
/// </list>
/// </summary>
internal static class UserDumper
{
    /// <summary>
    /// The privilege names <c>SHOW GRANTS</c> can report, which are the ones <c>GRANT</c> accepts back.
    /// The privilege text comes from the server and is written into a statement that runs at restore
    /// time, so it is held to this set rather than copied through.
    /// </summary>
    private static readonly HashSet<string> Privileges = new(StringComparer.Ordinal)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE TABLE", "DROP", "ALTER", "INDEX", "CREATE",
        "ALL PRIVILEGES",
    };

    /// <summary>How many times <c>--all-users</c> reads the catalog before it gives up on a stable view.</summary>
    private const int MaxCatalogReads = 3;

    /// <summary>
    /// Reads the accounts and their grants from the server and renders each grant as a statement. Both
    /// happen before any output file is opened, so a run that cannot read an account — or that meets a
    /// grant it cannot render — fails before it writes a dump that would be missing one.
    /// </summary>
    /// <exception cref="DumpException">A name is not an identifier, or the server refused a read.</exception>
    public static async Task<UserExport> FetchAsync(Options opts, CancellationToken cancellationToken = default)
    {
        if (opts.AllUsers && opts.Users.Any())
            throw new DumpException("--users and --all-users cannot both be given: --all-users already exports every account.");

        await using CamusConnection connection = await ConnectionFactory.CreateAsync(opts, cancellationToken: cancellationToken).ConfigureAwait(false);

        return opts.AllUsers
            ? await FetchAllAsync(connection, cancellationToken).ConfigureAwait(false)
            : await FetchNamedAsync(connection, ResolveNames(opts), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>--users</c> path: one <c>SHOW GRANTS FOR name</c> per named account. It is the only path a
    /// server older than <c>SHOW USERS</c> can answer, so it stays even though
    /// <see cref="FetchAllAsync"/> is cheaper on a current server.
    /// </summary>
    private static async Task<UserExport> FetchNamedAsync(CamusConnection connection, List<string> users, CancellationToken cancellationToken)
    {
        List<GrantStatement> statements = [];

        foreach (string user in users)
        {
            foreach (UserGrant grant in await FetchGrantsAsync(connection, user, cancellationToken).ConfigureAwait(false))
                statements.Add(new GrantStatement(user, RenderGrant(grant)));
        }

        return new UserExport(users, statements);
    }

    /// <summary>
    /// The <c>--all-users</c> path: every account from <c>SHOW USERS</c>, and every grant from
    /// <c>SHOW GRANTS FOR *</c>.
    ///
    /// <para><b>The two reads are checked against each other.</b> The server answers each statement from
    /// its own authoritative snapshot, and nothing ties the two snapshots together, so an account or a
    /// grant can change between them. <c>SHOW USERS</c> reports how many grants each account holds; the
    /// grant listing has to match that count for every account, and every grant has to belong to a
    /// listed account. When it does not, the catalog moved under the dump, and both are read again. The
    /// check is on counts, so a revoke and a grant on the same account between the two reads go
    /// unnoticed — this narrows the window, it does not close it. A dump that has to be exact is taken
    /// while nobody administers accounts.</para>
    /// </summary>
    private static async Task<UserExport> FetchAllAsync(CamusConnection connection, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            List<AccountRow> accounts = await ReadAccountsAsync(connection, cancellationToken).ConfigureAwait(false);
            List<UserGrant> grants = await ReadAllGrantsAsync(connection, cancellationToken).ConfigureAwait(false);

            if (TryMatch(accounts, grants, out List<UserGrant> matched, out string? mismatch))
            {
                List<string> users = accounts.Select(account => account.Name).ToList();
                List<GrantStatement> statements = matched.Select(grant => new GrantStatement(grant.User, RenderGrant(grant))).ToList();

                return new UserExport(
                    users,
                    statements,
                    superusers: accounts.Where(account => account.Superuser).Select(account => account.Name).ToList(),
                    passwordless: accounts.Where(account => !account.HasPassword).Select(account => account.Name).ToList());
            }

            if (attempt == MaxCatalogReads)
                throw new DumpException(
                    $"the account catalog kept changing while it was read ({mismatch}), {MaxCatalogReads} reads " +
                    "in a row. Take the dump while no account or grant is being changed.");
        }
    }

    /// <summary>One row of <c>SHOW USERS</c>, reduced to what the export uses.</summary>
    private sealed record AccountRow(string Name, bool Superuser, bool HasPassword, long GrantCount);

    private static async Task<List<AccountRow>> ReadAccountsAsync(CamusConnection connection, CancellationToken cancellationToken)
    {
        List<AccountRow> accounts = [];

        try
        {
            using CamusCommand cmd = connection.CreateSelectCommand("SHOW USERS");
            using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string name = reader.GetString(reader.GetOrdinal("user"));

                // The name reaches the dump as an identifier, so a name outside the grammar is refused here
                // with advice that fits an account, rather than with the table-shaped advice of the
                // generic identifier check.
                if (!SqlLiteral.IsPlainIdentifier(name))
                    throw new DumpException(
                        $"the server reported the account '{name}', which is not a CamusDB identifier " +
                        "([A-Za-z_][A-Za-z0-9_]*). It is refused rather than written into the dump, because a " +
                        "dump runs as SQL when it is restored. Export the other accounts with --users.");

                accounts.Add(new AccountRow(
                    name,
                    reader.GetColumnValue(reader.GetOrdinal("superuser")).BoolValue,
                    reader.GetColumnValue(reader.GetOrdinal("has_password")).BoolValue,
                    reader.GetColumnValue(reader.GetOrdinal("grants")).LongValue));
            }
        }
        catch (CamusException exception)
        {
            throw new DumpException(BuildListFailureMessage("SHOW USERS", exception));
        }

        return accounts;
    }

    private static async Task<List<UserGrant>> ReadAllGrantsAsync(CamusConnection connection, CancellationToken cancellationToken)
    {
        List<UserGrant> grants = [];

        try
        {
            using CamusCommand cmd = connection.CreateSelectCommand("SHOW GRANTS FOR *");
            using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                grants.Add(new UserGrant(
                    reader.GetString(reader.GetOrdinal("user")),
                    reader.GetString(reader.GetOrdinal("object")),
                    reader.GetString(reader.GetOrdinal("privileges"))));
            }
        }
        catch (CamusException exception)
        {
            throw new DumpException(BuildListFailureMessage("SHOW GRANTS FOR *", exception));
        }

        return grants;
    }

    /// <summary>
    /// Checks the grant listing against the account listing, and returns the grants with each one's
    /// account spelled as <c>SHOW USERS</c> spells it. The grant listing reports the normalized, lower-case
    /// name, while the account listing keeps the case the account was created with, which is the one worth
    /// restoring; the two are matched ignoring case, as the server matches them.
    /// </summary>
    private static bool TryMatch(List<AccountRow> accounts, List<UserGrant> grants, out List<UserGrant> matched, out string? mismatch)
    {
        Dictionary<string, AccountRow> byName = new(StringComparer.OrdinalIgnoreCase);

        foreach (AccountRow account in accounts)
            byName[account.Name] = account;

        Dictionary<string, long> counted = new(StringComparer.OrdinalIgnoreCase);
        matched = new List<UserGrant>(grants.Count);

        foreach (UserGrant grant in grants)
        {
            if (!byName.TryGetValue(grant.User, out AccountRow? account))
            {
                mismatch = $"a grant belongs to '{grant.User}', which SHOW USERS did not list";
                return false;
            }

            counted[account.Name] = counted.GetValueOrDefault(account.Name) + 1;
            matched.Add(grant with { User = account.Name });
        }

        foreach (AccountRow account in accounts)
        {
            long seen = counted.GetValueOrDefault(account.Name);

            if (seen != account.GrantCount)
            {
                mismatch = $"SHOW USERS reported {account.GrantCount} grants for '{account.Name}' and SHOW GRANTS FOR * listed {seen}";
                return false;
            }
        }

        mismatch = null;
        return true;
    }

    /// <summary>
    /// Explains a refused catalog listing. A server older than the listing statements rejects them, which
    /// on its own reads like a defect in this tool, so that case names the way round it. The rejection
    /// has two codes depending on the server's age: one that already has <c>SHOW VARIABLES</c> reads
    /// <c>SHOW USERS</c> as a misspelled <c>SHOW VARIABLES</c> and answers CADB0400, and one older still
    /// answers the syntax error CADB0406. CADB0400 also covers other invalid input, so the server's own
    /// message is kept in the text rather than replaced by a guess.
    /// </summary>
    private static string BuildListFailureMessage(string statement, CamusException exception)
    {
        string cause = exception.Code switch
        {
            "CADB0400" or "CADB0406" => $"the server rejected it ({exception.Code}: {exception.Message}). A server " +
                          "older than SHOW USERS and SHOW GRANTS FOR * answers this way; against one, name the " +
                          "accounts to export with --users instead",
            "CADB0517" => "listing every account needs a superuser. Authenticate as one with -u",
            _ => $"the server refused it with {exception.Code}: {exception.Message}",
        };

        return $"{statement} could not be read: {cause}.";
    }

    /// <summary>
    /// The accounts <c>--users</c> names, trimmed, de-duplicated and checked against the identifier
    /// grammar. CamusDB holds a user name to that grammar as well, so a name outside it cannot name an
    /// account and is refused here rather than written into the dump.
    /// </summary>
    private static List<string> ResolveNames(Options opts)
    {
        List<string> users = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in opts.Users)
        {
            string user = raw.Trim();

            if (user.Length == 0)
                continue;

            if (!SqlLiteral.IsPlainIdentifier(user))
                throw new DumpException(
                    $"--users names '{user}', which is not a CamusDB identifier ([A-Za-z_][A-Za-z0-9_]*). " +
                    "A user name follows the same grammar, so no account can carry that name.");

            if (seen.Add(user))
                users.Add(user);
        }

        if (users.Count == 0)
            throw new DumpException("--users needs at least one account name.");

        return users;
    }

    private static async Task<List<UserGrant>> FetchGrantsAsync(
        CamusConnection connection,
        string user,
        CancellationToken cancellationToken)
    {
        List<UserGrant> grants = [];

        try
        {
            using CamusCommand cmd = connection.CreateSelectCommand("SHOW GRANTS FOR " + SqlLiteral.Identifier(user, "user"));
            using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                grants.Add(new UserGrant(
                    user,
                    reader.GetString(reader.GetOrdinal("object")),
                    reader.GetString(reader.GetOrdinal("privileges"))));
            }
        }
        catch (CamusException exception)
        {
            throw new DumpException(BuildReadFailureMessage(user, exception));
        }

        return grants;
    }

    /// <summary>
    /// Explains a refused <c>SHOW GRANTS</c>. The server answers it for the calling account freely and
    /// for any other account only to a superuser, and it reports a name it does not know as an error, so
    /// those two are named as the causes to look at first.
    /// </summary>
    private static string BuildReadFailureMessage(string user, CamusException exception)
    {
        string cause = exception.Code switch
        {
            "CADB0513" => $"no account named '{user}' exists on this server. Check the name, or leave it out of --users",
            "CADB0517" => $"reading the grants of '{user}' needs a superuser. Authenticate as one with -u",
            _ => $"the server refused it with {exception.Code}: {exception.Message}",
        };

        return $"the grants of '{user}' could not be read: {cause}.";
    }

    /// <summary>
    /// Renders one <c>GRANT</c> statement. Both halves come from the server and both are checked before
    /// they reach the dump: the privileges against <see cref="Privileges"/>, and each name in the object
    /// against the identifier grammar.
    /// </summary>
    /// <exception cref="DumpException">The server reported a privilege or an object this cannot render.</exception>
    private static string RenderGrant(UserGrant grant)
        => $"GRANT {RenderPrivileges(grant)} ON {RenderObject(grant)} TO {SqlLiteral.Identifier(grant.User, "user")};";

    private static string RenderPrivileges(UserGrant grant)
    {
        List<string> names = [];

        foreach (string part in grant.Privileges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = part.ToUpperInvariant();

            if (!Privileges.Contains(name))
                throw new DumpException(
                    $"the server reported the privilege '{part}' on {grant.Object} for user '{grant.User}', " +
                    "which is not one this version of camus-dump knows how to write as a GRANT. " +
                    "Record that grant by hand, and leave the account out of --users.");

            names.Add(name);
        }

        if (names.Count == 0)
            throw new DumpException(
                $"the server reported a grant on {grant.Object} for user '{grant.User}' with no privileges.");

        return string.Join(", ", names);
    }

    /// <summary>
    /// Renders the object half of a <c>GRANT</c> from the <c>db.table</c> / <c>db.*</c> / <c>*.*</c> text
    /// <c>SHOW GRANTS</c> prints. The three forms are exactly the three the grammar accepts.
    /// </summary>
    private static string RenderObject(UserGrant grant)
    {
        string[] parts = grant.Object.Split('.');

        if (parts.Length == 2 && parts[0] == "*" && parts[1] == "*")
            return "*.*";

        if (parts.Length == 2 && parts[0] != "*")
        {
            string database = SqlLiteral.Identifier(parts[0], "database");

            return parts[1] == "*"
                ? database + ".*"
                : database + "." + SqlLiteral.Identifier(parts[1], "table");
        }

        throw new DumpException(
            $"the server reported the grant object '{grant.Object}' for user '{grant.User}', which is " +
            "none of the three forms a GRANT accepts (*.*, database.*, database.table).");
    }
}

/// <summary>
/// The accounts and grants one run exports, and the two blocks of SQL they are written as.
///
/// <para>The blocks are separate because their restore order is not the same. An account has to exist
/// before anything is granted to it, and a <c>GRANT</c> resolves its object to an immutable id, so the
/// database or table has to exist before the grant runs. Accounts therefore go first in the file and
/// grants last; under <c>--output-directory</c> they go into <c>users.sql</c> and <c>grants.sql</c>,
/// which is the order to run them in around the per-database files.</para>
/// </summary>
internal sealed class UserExport
{
    private readonly IReadOnlyList<string> users;

    private readonly IReadOnlyList<GrantStatement> grants;

    /// <summary>The accounts that were superusers on the source, or null when the source did not say.</summary>
    private readonly IReadOnlyList<string>? superusers;

    /// <summary>The accounts that had no password on the source, or null when the source did not say.</summary>
    private readonly IReadOnlyList<string>? passwordless;

    /// <param name="users">The accounts, in the order they are written.</param>
    /// <param name="grants">The rendered grants of those accounts.</param>
    /// <param name="superusers">Known only from <c>SHOW USERS</c>; null on the <c>--users</c> path.</param>
    /// <param name="passwordless">Known only from <c>SHOW USERS</c>; null on the <c>--users</c> path.</param>
    public UserExport(
        IReadOnlyList<string> users,
        IReadOnlyList<GrantStatement> grants,
        IReadOnlyList<string>? superusers = null,
        IReadOnlyList<string>? passwordless = null)
    {
        this.users = users;
        this.grants = grants;
        this.superusers = superusers;
        this.passwordless = passwordless;
    }

    /// <summary>
    /// Writes the <c>CREATE USER</c> block, headed by what an operator has to do afterwards. The header
    /// is not decoration: an account restored from here cannot log in until a password is set on it, and
    /// an account that was a superuser does not come back as one.
    /// </summary>
    public void WriteUsers(TextWriter output)
    {
        output.WriteLine($"-- Users: {users.Count}. No password is exported, because none can be: the server stores a");
        output.WriteLine("-- salted verifier and CREATE USER takes cleartext only. Every account below is created");
        output.WriteLine("-- without one, and the server refuses every login to an account with no password, so set");
        output.WriteLine("-- one on each account after the restore:");
        output.WriteLine("--   ALTER USER `name` IDENTIFIED BY 'the password';");
        output.WriteLine("-- Superuser status is not exported either. No statement grants it: the server makes only");
        output.WriteLine("-- the bootstrap user a superuser, at start, so an account that was one comes back as an");
        output.WriteLine("-- ordinary account.");

        if (superusers is not null)
            output.WriteLine("-- Superusers on the source: " + NameList(superusers) + ".");

        if (passwordless is not null)
            output.WriteLine("-- Accounts with no password on the source, so none to set: " + NameList(passwordless) + ".");

        foreach (string user in users)
            output.WriteLine($"CREATE USER IF NOT EXISTS {SqlLiteral.Identifier(user, "user")};");

        output.WriteLine();
    }

    private static string NameList(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

    /// <summary>
    /// Writes the <c>GRANT</c> block. An account with no grant is named in a comment, so the file says
    /// that the account was read and held nothing, rather than leaving that to be guessed.
    /// </summary>
    public void WriteGrants(TextWriter output)
    {
        output.WriteLine($"-- Grants: {grants.Count}. Run these after the databases, the tables and the views exist. A");
        output.WriteLine("-- GRANT resolves its object to an immutable id, so it fails while the object is missing.");

        foreach (string user in users)
        {
            List<GrantStatement> forUser = grants.Where(grant => grant.User == user).ToList();

            if (forUser.Count == 0)
            {
                output.WriteLine($"-- {user}: no grants.");
                continue;
            }

            foreach (GrantStatement grant in forUser)
                output.WriteLine(grant.Statement);
        }

        output.WriteLine();
    }
}
