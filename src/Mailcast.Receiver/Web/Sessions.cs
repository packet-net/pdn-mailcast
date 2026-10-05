using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mailcast.Receiver.Web;

/// <summary>
/// The page's signed-in browsers: each has a random token in a cookie, and this keeps only a hash
/// of it, in memory and in a file in the state directory so a restart signs nobody out.
/// </summary>
/// <remarks>
/// <para>Each hash is HMAC-SHA256 keyed by the token over the page password, so a token is only
/// good under the password it was given under, and the file holds nothing a token or the password
/// can be got back from. The password is always the one in force now, asked for each time, and a
/// change of it signs everyone out: the next call here notices it and forgets every session at once.</para>
/// </remarks>
internal sealed class SessionStore
{
    /// <summary>How long a sign-in lasts without "keep me signed in": the browser forgets it when it closes, and this is the longest it lasts even if the browser does not.</summary>
    public static readonly TimeSpan BrowserSessionLifetime = TimeSpan.FromDays(1);

    /// <summary>How long a sign-in with "keep me signed in" lasts.</summary>
    public static readonly TimeSpan RememberedLifetime = TimeSpan.FromDays(30);

    /// <summary>The most sessions kept; past it the one nearest its end goes.</summary>
    public const int MaxSessions = 200;

    /// <summary>Bytes in a token: 256 bits.</summary>
    public const int TokenBytes = 32;

    /// <summary>The name of the file in the state directory.</summary>
    public const string FileName = "web-sessions.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Func<string> _currentPassword;
    private string _password;

    /// <summary>One signed-in browser: when its sign-in ends, and whether it asked to be kept signed in.</summary>
    public sealed record Session(DateTimeOffset Expires, bool Remembered);

    private sealed record Saved(string Hash, DateTimeOffset Expires, bool Remembered);

    /// <summary>
    /// Loads the sessions from <paramref name="path"/> (null keeps them in memory only), under the
    /// page password <paramref name="currentPassword"/> gives.
    /// </summary>
    public SessionStore(string? path, Func<string> currentPassword, TimeProvider time, Action<string> log)
    {
        _path = path;
        _currentPassword = currentPassword;
        _password = currentPassword();
        _time = time;
        _log = log;
        Load();
    }

    /// <summary>How many sessions are held, for tests.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Starts a session under the password in force and gives its token, for the cookie.</summary>
    public (string Token, Session Session) Create(bool remembered)
    {
        byte[] token = RandomNumberGenerator.GetBytes(TokenBytes);
        var now = _time.GetUtcNow();
        var session = new Session(now + (remembered ? RememberedLifetime : BrowserSessionLifetime), remembered);
        lock (_gate)
        {
            string password = NoticePassword();
            Prune(now);
            while (_sessions.Count >= MaxSessions)
            {
                _sessions.Remove(_sessions.MinBy(s => s.Value.Expires).Key);
            }
            _sessions[Hash(token, password)] = session;
            Save();
        }
        return (Base64Url(token), session);
    }

    /// <summary>The session <paramref name="token"/> is for, if it is one, still running, under the password in force.</summary>
    public Session? Find(string? token)
    {
        if (Decode(token) is not { } bytes)
        {
            return null;
        }
        lock (_gate)
        {
            string hash = Hash(bytes, NoticePassword());
            if (!_sessions.TryGetValue(hash, out var session))
            {
                return null;
            }
            if (session.Expires <= _time.GetUtcNow())
            {
                _sessions.Remove(hash);
                Save();
                return null;
            }
            return session;
        }
    }

    /// <summary>Ends the session <paramref name="token"/> is for, if any; false if there was none.</summary>
    public bool Remove(string? token)
    {
        if (Decode(token) is not { } bytes)
        {
            return false;
        }
        lock (_gate)
        {
            if (!_sessions.Remove(Hash(bytes, NoticePassword())))
            {
                return false;
            }
            Save();
            return true;
        }
    }

    /// <summary>Signs everyone out if the password is not the one the sessions were made under.</summary>
    public void NoticePasswordChange()
    {
        lock (_gate)
        {
            NoticePassword();
        }
    }

    /// <summary>The password in force; if it is not the one the sessions were made under, they are all forgotten.</summary>
    private string NoticePassword()
    {
        string password = _currentPassword();
        if (string.Equals(password, _password, StringComparison.Ordinal))
        {
            return password;
        }
        _password = password;
        if (_sessions.Count > 0)
        {
            _sessions.Clear();
            Save();
            _log("web: the page password has changed, so every browser signed in has been signed out");
        }
        return password;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var gone in _sessions.Where(s => s.Value.Expires <= now).Select(s => s.Key).ToList())
        {
            _sessions.Remove(gone);
        }
    }

    private static string Hash(byte[] token, string password) =>
        Convert.ToHexString(HMACSHA256.HashData(token, Encoding.UTF8.GetBytes(password)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A token's bytes, or null for anything that is not one.</summary>
    private static byte[]? Decode(string? token)
    {
        // 32 bytes is 43 characters of unpadded base64url.
        if (token is null || token.Length != 43 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return null;
        }
        try
        {
            byte[] bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
            return bytes.Length == TokenBytes ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }
        try
        {
            var saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllBytes(_path), Json) ?? [];
            var now = _time.GetUtcNow();
            foreach (var s in saved.Where(s => s.Hash is { Length: 64 } && s.Expires > now).Take(MaxSessions))
            {
                _sessions[s.Hash] = new Session(s.Expires, s.Remembered);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"web: cannot read {Ascii.Clean(_path)}, so nobody is signed in: {Ascii.Clean(e.Message)}");
        }
    }

    /// <summary>Writes the sessions, owner-only, to a temporary name and renames it over the old; a failure is logged and the sessions carry on in memory.</summary>
    private void Save()
    {
        if (_path is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string tmp = _path + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(tmp, options))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                var saved = _sessions.Select(s => new Saved(s.Key, s.Value.Expires, s.Value.Remembered)).ToList();
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(saved, Json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"web: cannot write {Ascii.Clean(_path)}; sign-ins will not last a restart: {Ascii.Clean(e.Message)}");
        }
    }
}

/// <summary>
/// Slows down password guessing: each wrong password is answered after <see cref="FailureDelay"/>,
/// and <see cref="MaxFailures"/> wrong ones from one address within <see cref="FailureWindow"/>
/// refuse every sign-in from it, right or wrong, for <see cref="LockoutTime"/>.
/// </summary>
internal sealed class SignInThrottle(TimeProvider time, Action<string> log)
{
    /// <summary>How long each wrong password waits before it is answered.</summary>
    public static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(1);

    /// <summary>Wrong passwords from one address, within <see cref="FailureWindow"/>, that lock it out.</summary>
    public const int MaxFailures = 5;

    /// <summary>The window <see cref="MaxFailures"/> are counted in.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    /// <summary>How long an address is locked out for.</summary>
    public static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(10);

    /// <summary>The most addresses remembered; past it the quietest are forgotten.</summary>
    public const int MaxAddresses = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<System.Net.IPAddress, Tally> _tallies = [];

    private sealed class Tally
    {
        public List<DateTimeOffset> Failures { get; } = [];

        public DateTimeOffset? LockedUntil { get; set; }
    }

    /// <summary>What became of a password given.</summary>
    public enum Verdict
    {
        /// <summary>The right password.</summary>
        Right,

        /// <summary>The wrong password.</summary>
        Wrong,

        /// <summary>Not looked at: the address is locked out.</summary>
        LockedOut,
    }

    /// <summary>
    /// Checks <paramref name="given"/> against <paramref name="password"/> for a request from
    /// <paramref name="from"/>, in constant time, and counts it. While locked out the answer is
    /// <see cref="Verdict.LockedOut"/> whatever was given; <paramref name="retryAfter"/> then says for how long.
    /// </summary>
    public Verdict Check(System.Net.IPAddress from, string given, string password, out TimeSpan retryAfter)
    {
        from = from.IsIPv4MappedToIPv6 ? from.MapToIPv4() : from;
        var now = time.GetUtcNow();
        retryAfter = TimeSpan.Zero;
        lock (_gate)
        {
            if (_tallies.TryGetValue(from, out var tally) && tally.LockedUntil is { } until)
            {
                if (until > now)
                {
                    retryAfter = until - now;
                    return Verdict.LockedOut;
                }
                _tallies.Remove(from);
                tally = null;
            }
            // Hashed first, so the comparison takes as long whatever the lengths.
            bool right = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(password)));
            if (right)
            {
                _tallies.Remove(from);
                return Verdict.Right;
            }
            if (tally is null)
            {
                Forget(now);
                tally = new Tally();
                _tallies[from] = tally;
            }
            tally.Failures.RemoveAll(t => now - t >= FailureWindow);
            tally.Failures.Add(now);
            if (tally.Failures.Count >= MaxFailures)
            {
                tally.LockedUntil = now + LockoutTime;
                tally.Failures.Clear();
                log($"web: {MaxFailures} wrong passwords from {from} within {FailureWindow.TotalMinutes:0} minutes; sign-in from there is refused for {LockoutTime.TotalMinutes:0} minutes");
            }
            return Verdict.Wrong;
        }
    }

    /// <summary>Makes room for one more address: forgets those with nothing recent, then the quietest.</summary>
    private void Forget(DateTimeOffset now)
    {
        if (_tallies.Count < MaxAddresses)
        {
            return;
        }
        foreach (var quiet in _tallies.Where(t => t.Value.LockedUntil is not { } u ? t.Value.Failures.All(f => now - f >= FailureWindow) : u <= now).Select(t => t.Key).ToList())
        {
            _tallies.Remove(quiet);
        }
        while (_tallies.Count >= MaxAddresses)
        {
            _tallies.Remove(_tallies.MinBy(t => t.Value.LockedUntil ?? t.Value.Failures.LastOrDefault()).Key);
        }
    }
}
