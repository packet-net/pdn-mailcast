using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>The status page's sign-in on the network: the page, its sessions, the lockout, and HTTP Basic for scripts.</summary>
public class SignInTests
{
    private const string Password = "letmein-7Q";
    private const string Wrong = "guess-4Z";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A receiver on the network with its page started, on a fake clock, its log kept.</summary>
    private sealed class Receiver : IAsyncDisposable
    {
        public required ReceiverHost Host { get; init; }

        public required StatusPage Page { get; init; }

        public required HttpClient Http { get; init; }

        public required string ConfigPath { get; init; }

        public required List<string> Log { get; init; }

        public required int Port { get; init; }

        /// <summary>Every body and header the page has answered with, to look for the password in.</summary>
        public List<string> Answers { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            var answer = await Http.SendAsync(request);
            lock (Answers)
            {
                Answers.Add(answer.Headers.ToString());
                Answers.Add(answer.Content.Headers.ToString());
            }
            string body = await answer.Content.ReadAsStringAsync();
            lock (Answers)
            {
                Answers.Add(body);
            }
            return answer;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Page.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private static ReceiverConfig Config(string dir, int port, string password) => new()
    {
        Audio = "wav:/nonexistent.wav",
        StateDirectory = dir,
        Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "secret" },
        Web = new WebSettings { Port = port, Lan = password.Length > 0, Password = password },
    };

    /// <summary>See StatusPageTests for why it may ask for a port twice. No cookies are kept by the client: each test gives them itself.</summary>
    private static async Task<Receiver> StartAsync(string dir, FakeTimeProvider time, string password = Password, Func<TimeSpan, Task>? pause = null)
    {
        string path = System.IO.Path.Combine(dir, "receiver.json");
        var log = new List<string>();
        void Log(string line)
        {
            lock (log)
            {
                log.Add(line);
            }
        }
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var config = Config(dir, port, password);
            config.Save(path);
            var host = new ReceiverHost(config, time, Log);
            var page = new StatusPage(host, path, Log) { FailureDelay = pause is null ? TimeSpan.Zero : SignInThrottle.FailureDelay, Pause = pause };
            try
            {
                page.Start();
                var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                };
                return new Receiver { Host = host, Page = page, Http = http, ConfigPath = path, Log = log, Port = port };
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                await page.DisposeAsync();
                await host.DisposeAsync();
            }
        }
    }

    /// <summary>A request; <paramref name="browser"/> is the Sec-Fetch-Mode a browser would send with it, which a script does not.</summary>
    private static HttpRequestMessage Request(HttpMethod method, string url, string? cookie = null, object? json = null, string? origin = null, string? browser = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (browser is not null)
        {
            request.Headers.Add("Sec-Fetch-Mode", browser);
        }
        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return request;
    }

    private static Task<HttpResponseMessage> SignInAsync(Receiver r, string password, bool remember = false, string? origin = null) =>
        r.SendAsync(Request(HttpMethod.Post, "login", json: new { password, remember }, origin: origin));

    private static Task<HttpResponseMessage> GetAsync(Receiver r, string url, string? cookie = null, bool browser = false) =>
        r.SendAsync(Request(HttpMethod.Get, url, cookie, browser: browser ? "navigate" : null));

    private static string? SetCookie(HttpResponseMessage answer) =>
        answer.Headers.TryGetValues("Set-Cookie", out var values) ? values.Single() : null;

    /// <summary>The <c>name=value</c> part of a Set-Cookie, as a browser would send it back.</summary>
    private static string Cookie(HttpResponseMessage answer) => SetCookie(answer)!.Split(';')[0];

    private static async Task<string> CookieAsync(Receiver r, bool remember = false)
    {
        var answer = await SignInAsync(r, Password, remember);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return Cookie(answer);
    }

    private static string Basic(string password) => "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("any:" + password));

    private static async Task<string> ErrorAsync(HttpResponseMessage answer) =>
        (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    [Fact]
    public async Task Page_WithoutASession_IsTheSignInPage_AndTheRightPasswordSignsIn()
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start));

        var page = await GetAsync(r, "/", browser: true);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("name=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("Keep me signed in on this device", html, StringComparison.Ordinal);
        Assert.Contains("action=\"login\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("api/status", html, StringComparison.Ordinal);
        Assert.DoesNotContain("broadcast", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch("[^\\x00-\\x7F]", html);
        Assert.False(page.Headers.Contains("WWW-Authenticate"));
        // A page further down posts to the same /login.
        Assert.Contains("action=\"../login\"", await (await GetAsync(r, "/waterfall/", browser: true)).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var answer = await SignInAsync(r, Password);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        string setCookie = SetCookie(answer)!;
        Assert.StartsWith(r.Page.CookieName + "=", setCookie, StringComparison.Ordinal);
        Assert.Contains("; HttpOnly", setCookie, StringComparison.Ordinal);
        Assert.Contains("; SameSite=Strict", setCookie, StringComparison.Ordinal);
        Assert.Contains("; Path=/", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("Secure", setCookie, StringComparison.Ordinal);
        // Without "keep me signed in", the browser forgets it when it closes.
        Assert.DoesNotContain("Max-Age", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("Expires", setCookie, StringComparison.Ordinal);
        // 256 bits, unpadded base64url.
        Assert.Matches("^[A-Za-z0-9_-]{43}$", Cookie(answer).Split('=', 2)[1]);

        string cookie = Cookie(answer);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(r, "api/status", cookie)).StatusCode);
        string signedIn = await (await GetAsync(r, "/", cookie, browser: true)).Content.ReadAsStringAsync();
        Assert.Contains("api/status", signedIn, StringComparison.Ordinal);
        Assert.Contains("Sign out", signedIn, StringComparison.Ordinal);
        var settings = await (await GetAsync(r, "api/settings", cookie)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(settings.GetProperty("web").GetProperty("passwordSet").GetBoolean());
        Assert.Contains(r.Log, l => l.StartsWith("web: signed in from 127.0.0.1", StringComparison.Ordinal));
        // Once in, /login goes back to the page.
        var again = await GetAsync(r, "login", cookie, browser: true);
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
    }

    [Fact]
    public async Task SignIn_WithTheWrongPassword_IsRefused_AsJsonOrAsAForm()
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start));

        var wrong = await SignInAsync(r, Wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("That password is not right.", await ErrorAsync(wrong));
        Assert.Null(SetCookie(wrong));
        var empty = await r.SendAsync(Request(HttpMethod.Post, "login", json: new { remember = true }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Null(SetCookie(empty));

        // The sign-in page without its script posts a plain form.
        var formWrong = await r.SendAsync(new HttpRequestMessage(HttpMethod.Post, "login") { Content = new FormUrlEncodedContent([new("password", Wrong)]) });
        Assert.Equal(HttpStatusCode.Unauthorized, formWrong.StatusCode);
        Assert.Contains("That password is not right.", await formWrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(SetCookie(formWrong));
        var formRight = await r.SendAsync(new HttpRequestMessage(HttpMethod.Post, "login")
        {
            Content = new FormUrlEncodedContent([new("password", Password), new("remember", "on")]),
        });
        Assert.Equal(HttpStatusCode.SeeOther, formRight.StatusCode);
        Assert.Equal("./", formRight.Headers.Location!.OriginalString);
        Assert.Contains("Max-Age=", SetCookie(formRight), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(r, "api/status", Cookie(formRight))).StatusCode);

        // A made-up cookie is no session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(r, "api/status", $"{r.Page.CookieName}={new string('A', 43)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(r, "api/status", $"{r.Page.CookieName}=junk")).StatusCode);
    }

    [Fact]
    public async Task SignIn_NotJsonOrAFormOrTooLarge_IsRefused()
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start));

        var plain = await r.SendAsync(new HttpRequestMessage(HttpMethod.Post, "login") { Content = new StringContent($"{{\"password\":\"{Password}\"}}", System.Text.Encoding.UTF8, "text/plain") });
        var huge = await r.SendAsync(new HttpRequestMessage(HttpMethod.Post, "login") { Content = new StringContent(new string(' ', StatusPage.MaxFormBytes + 1), System.Text.Encoding.UTF8, "application/json") });

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        Assert.Null(SetCookie(plain));
        Assert.Null(SetCookie(huge));
    }

    [Fact]
    public async Task KeepMeSignedIn_Lasts30Days_AndWithoutItADay()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        await using var r = await StartAsync(dir.Path, time);

        var remembered = await SignInAsync(r, Password, remember: true);
        Assert.Contains("; Max-Age=2592000", SetCookie(remembered), StringComparison.Ordinal);
        string kept = Cookie(remembered);
        string forBrowser = await CookieAsync(r);

        time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(r, "api/status", forBrowser)).StatusCode);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(r, "api/status", forBrowser)).StatusCode);

        time.Advance(TimeSpan.FromDays(29) - TimeSpan.FromSeconds(1) - TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(r, "api/status", kept)).StatusCode);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(r, "api/status", kept)).StatusCode);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(r, "api/status", kept)).StatusCode);
        Assert.Equal(0, r.Page.Sessions.Count);
    }

    [Fact]
    public async Task Session_SurvivesARestart_AndOnlyItsHashIsOnDisk()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        string cookie;
        await using (var first = await StartAsync(dir.Path, time))
        {
            cookie = await CookieAsync(first, remember: true);
        }

        string file = System.IO.Path.Combine(dir.Path, SessionStore.FileName);
        string saved = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain(cookie.Split('=', 2)[1], saved, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, saved, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }

        time.Advance(TimeSpan.FromDays(3));
        await using var second = await StartAsync(dir.Path, time);
        // The port is new, so is the cookie's name; the token is what is kept.
        string token = cookie.Split('=', 2)[1];
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(second, "api/status", $"{second.Page.CookieName}={token}")).StatusCode);
    }

    [Fact]
    public async Task PasswordChange_SignsEveryBrowserOut_ButTheOneThatChangedIt()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        string other;
        string mine;
        string newCookie;
        const string NewPassword = "brand-new-9K";
        await using (var r = await StartAsync(dir.Path, time))
        {
            other = await CookieAsync(r);
            mine = await CookieAsync(r, remember: true);
            object Form(string? current) => new
            {
                audio = "wav:/nonexistent.wav", type = "linBpq", host = "127.0.0.1", port = 8011, login = "Q0CAST", password = "", command = "BBS",
                pagePassword = NewPassword, currentPagePassword = current,
            };

            var noCurrent = await r.SendAsync(Request(HttpMethod.Post, "api/settings", mine, Form(Wrong)));
            Assert.Equal(HttpStatusCode.BadRequest, noCurrent.StatusCode);
            Assert.Contains("current one", await ErrorAsync(noCurrent), StringComparison.Ordinal);
            Assert.Equal(Password, ReceiverConfig.Load(r.ConfigPath).Web.Password);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(r, "api/status", other)).StatusCode);

            var changed = await r.SendAsync(Request(HttpMethod.Post, "api/settings", mine, Form(Password)));
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.Equal(NewPassword, ReceiverConfig.Load(r.ConfigPath).Web.Password);
            Assert.Equal(NewPassword, r.Host.Config.Web.Password);
            // The browser that changed it is signed in again, still kept signed in.
            Assert.Contains("Max-Age=", SetCookie(changed), StringComparison.Ordinal);
            newCookie = Cookie(changed);

            Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(r, "api/status", other)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(r, "api/status", mine)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(r, "api/status", newCookie)).StatusCode);
            Assert.Equal(1, r.Page.Sessions.Count);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(r, Password)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SignInAsync(r, NewPassword)).StatusCode);
            Assert.DoesNotContain(r.Log, l => l.Contains(NewPassword, StringComparison.Ordinal) || l.Contains(Password, StringComparison.Ordinal));
        }

        // A password changed in the file while the receiver was stopped does the same.
        await using var restarted = await StartAsync(dir.Path, time, password: "edited-by-hand-2B");
        string token = newCookie.Split('=', 2)[1];
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(restarted, "api/status", $"{restarted.Page.CookieName}={token}")).StatusCode);
    }

    [Fact]
    public async Task FiveWrongPasswords_LockTheAddressOutForTenMinutes_LoggedOnce()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        await using var r = await StartAsync(dir.Path, time);

        for (int i = 0; i < SignInThrottle.MaxFailures; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(r, Wrong)).StatusCode);
        }
        var locked = await SignInAsync(r, Password);
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.Contains("Try again in 10 minutes", await ErrorAsync(locked), StringComparison.Ordinal);
        Assert.Equal("600", locked.Headers.GetValues("Retry-After").Single());
        Assert.Null(SetCookie(locked));
        // HTTP Basic is locked out too, so a script cannot guess around it.
        var basic = Request(HttpMethod.Get, "api/status");
        basic.Headers.TryAddWithoutValidation("Authorization", Basic(Password));
        Assert.Equal((HttpStatusCode)429, (await r.SendAsync(basic)).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await SignInAsync(r, Wrong)).StatusCode);

        string[] lockouts = [.. r.Log.Where(l => l.Contains("wrong passwords from", StringComparison.Ordinal))];
        Assert.Equal(["web: 5 wrong passwords from 127.0.0.1 within 5 minutes; sign-in from there is refused for 10 minutes"], lockouts);

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal((HttpStatusCode)429, (await SignInAsync(r, Password)).StatusCode);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(r, Password)).StatusCode);
        Assert.Single(r.Log, l => l.Contains("wrong passwords from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongPasswords_SpreadOutMoreThanFiveMinutes_DoNotLockOut()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        await using var r = await StartAsync(dir.Path, time);

        for (int i = 0; i < 2 * SignInThrottle.MaxFailures; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(r, Wrong)).StatusCode);
            time.Advance(TimeSpan.FromSeconds(76)); // four in any five minutes
        }

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(r, Password)).StatusCode);
        Assert.DoesNotContain(r.Log, l => l.Contains("wrong passwords from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongPassword_IsAnsweredOnlyAfterADelay_TheRightOneAtOnce()
    {
        using var dir = new TempDirectory();
        var asked = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start), pause: wait =>
        {
            asked.TrySetResult(wait);
            return release.Task;
        });

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(r, Password)).StatusCode);
        Assert.False(asked.Task.IsCompleted); // the right password does not wait

        var wrong = SignInAsync(r, Wrong);
        Assert.Equal(SignInThrottle.FailureDelay, await asked.Task);
        Assert.False(wrong.IsCompleted); // held until the wait is over
        release.SetResult();
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong).StatusCode);
    }

    [Fact]
    public async Task BasicAuth_StillWorksForScripts_AndOnlyScriptsAreAskedForIt()
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start));

        var right = Request(HttpMethod.Get, "api/mail");
        right.Headers.TryAddWithoutValidation("Authorization", Basic(Password));
        Assert.Equal(HttpStatusCode.OK, (await r.SendAsync(right)).StatusCode);
        var post = Request(HttpMethod.Post, "api/mail/resend", json: new { id = "nonesuch" });
        post.Headers.TryAddWithoutValidation("Authorization", Basic(Password));
        Assert.Equal(HttpStatusCode.BadRequest, (await r.SendAsync(post)).StatusCode); // let in, and told the id is no good
        var wrong = Request(HttpMethod.Get, "api/status");
        wrong.Headers.TryAddWithoutValidation("Authorization", Basic(Wrong));
        var refused = await r.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("That password is not right.", await ErrorAsync(refused));

        // A script that waits to be asked is asked; a browser is not, or it would show its own password box.
        var script = await GetAsync(r, "api/status");
        Assert.Contains("Basic", script.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        var browser = Request(HttpMethod.Get, "api/status");
        browser.Headers.Add("Sec-Fetch-Mode", "cors");
        var fromBrowser = await r.SendAsync(browser);
        Assert.Equal(HttpStatusCode.Unauthorized, fromBrowser.StatusCode);
        Assert.Empty(fromBrowser.Headers.WwwAuthenticate);

        // A script asking for a page is asked too, not given the sign-in page.
        foreach (string url in new[] { "/", "/waterfall/" })
        {
            var page = await GetAsync(r, url);
            Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
            Assert.Contains("Basic", page.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("name=\"password\"", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var pageWithBasic = Request(HttpMethod.Get, "/");
        pageWithBasic.Headers.TryAddWithoutValidation("Authorization", Basic(Password));
        var shown = await r.SendAsync(pageWithBasic);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Contains("api/status", await shown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browser_RememberingABasicLogin_StillSeesTheSignInPage_CanSignOut_AndIsNeverLockedOut()
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start));
        HttpRequestMessage FromBrowser(string url, string basic, string? cookie = null, string mode = "navigate")
        {
            var request = Request(HttpMethod.Get, url, cookie, browser: mode);
            request.Headers.TryAddWithoutValidation("Authorization", Basic(basic));
            return request;
        }

        // The right password by Basic does not let a browser in: it gets the sign-in page, with no complaint.
        var page = await r.SendAsync(FromBrowser("/", Password));
        string html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("name=\"password\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("not right", html, StringComparison.Ordinal);
        var api = await r.SendAsync(FromBrowser("api/status", Password, mode: "cors"));
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Empty(api.Headers.WwwAuthenticate);

        // Signed in, then out: the remembered Basic login does not keep it in.
        string cookie = await CookieAsync(r);
        Assert.Contains("api/status", await (await r.SendAsync(FromBrowser("/", Password, cookie))).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await r.SendAsync(Request(HttpMethod.Post, "logout", cookie, new { }, browser: "cors"))).StatusCode);
        Assert.Contains("name=\"password\"", await (await r.SendAsync(FromBrowser("/", Password, cookie))).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // An old password remembered after a change is not counted against the address, however often the page polls.
        for (int i = 0; i < 4 * SignInThrottle.MaxFailures; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await r.SendAsync(FromBrowser("api/status", Wrong, mode: "cors"))).StatusCode);
        }
        Assert.DoesNotContain("not right", await (await r.SendAsync(FromBrowser("/", Wrong))).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(r.Log, l => l.Contains("wrong passwords", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(r, Password)).StatusCode);
    }

    [Fact]
    public void EachIpv6Address_IsCountedOnItsOwn_EvenInOneSlash64_AndIpv4MappedAsIpv4()
    {
        var time = new FakeTimeProvider(Start);
        var log = new List<string>();
        var throttle = new SignInThrottle(time, log.Add);

        // One device on a home network guesses wrong until it is locked out.
        for (int i = 0; i < SignInThrottle.MaxFailures; i++)
        {
            Assert.Equal(SignInThrottle.Verdict.Wrong, throttle.Check(IPAddress.Parse("2001:db8:0:1::5"), Wrong, Password, out _));
        }
        Assert.Equal(SignInThrottle.Verdict.LockedOut, throttle.Check(IPAddress.Parse("2001:db8:0:1::5"), Password, Password, out var wait));
        Assert.Equal(SignInThrottle.LockoutTime, wait);
        Assert.Equal(["web: 5 wrong passwords from 2001:db8:0:1::5 within 5 minutes; sign-in from there is refused for 10 minutes"], log);

        // Another device in the same /64, and one on the same link-local network, are not.
        Assert.Equal(SignInThrottle.Verdict.Right, throttle.Check(IPAddress.Parse("2001:db8:0:1::6"), Password, Password, out _));
        for (int i = 0; i < SignInThrottle.MaxFailures - 1; i++)
        {
            Assert.Equal(SignInThrottle.Verdict.Wrong, throttle.Check(IPAddress.Parse("fe80::1"), Wrong, Password, out _));
        }
        Assert.Equal(SignInThrottle.Verdict.Right, throttle.Check(IPAddress.Parse("fe80::2"), Password, Password, out _));
        Assert.Equal(SignInThrottle.Verdict.Wrong, throttle.Check(IPAddress.Parse("fe80::2"), Wrong, Password, out _));
        Assert.Single(log);

        // An IPv4 address in IPv6 form is the same address.
        for (int i = 0; i < SignInThrottle.MaxFailures; i++)
        {
            var from = IPAddress.Parse(i % 2 == 0 ? "192.168.1.9" : "::ffff:192.168.1.9");
            Assert.Equal(SignInThrottle.Verdict.Wrong, throttle.Check(from, Wrong, Password, out _));
        }
        Assert.Equal(SignInThrottle.Verdict.LockedOut, throttle.Check(IPAddress.Parse("192.168.1.9"), Password, Password, out _));
    }

    [Fact]
    public void ManyAddresses_SpendTheBudgetForEverywhere_AndThenEverySignInIsSlowed()
    {
        var time = new FakeTimeProvider(Start);
        var log = new List<string>();
        var throttle = new SignInThrottle(time, log.Add);
        int next = 0;
        IPAddress Fresh() { next++; return new IPAddress([10, (byte)(next >> 16), (byte)(next >> 8), (byte)next]); }

        for (int i = 0; i < SignInThrottle.GlobalBudget; i++)
        {
            Assert.Equal(SignInThrottle.Verdict.Wrong, throttle.Check(Fresh(), Wrong, Password, out _));
        }
        Assert.Empty(log);

        // From now on, one attempt each 3 seconds from anywhere, right or wrong.
        Assert.Equal(SignInThrottle.Verdict.Wrong, throttle.Check(Fresh(), Wrong, Password, out _));
        Assert.Equal(SignInThrottle.Verdict.Busy, throttle.Check(Fresh(), Password, Password, out var wait));
        Assert.Equal(SignInThrottle.GlobalSpacing, wait);
        time.Advance(SignInThrottle.GlobalSpacing);
        Assert.Equal(SignInThrottle.Verdict.Right, throttle.Check(Fresh(), Password, Password, out _));
        Assert.Single(log, l => l.Contains("from all addresses together", StringComparison.Ordinal));

        // Thousands of addresses, far past what the table of addresses holds, guessing as fast as
        // they can for 20 minutes: only one guess each 3 seconds is looked at.
        int looked = 0;
        var started = time.GetUtcNow();
        for (int step = 0; step < 20 * 60 * 10; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            if (throttle.Check(Fresh(), Wrong, Password, out _) != SignInThrottle.Verdict.Busy)
            {
                looked++;
            }
        }
        Assert.True(next > SignInThrottle.MaxAddresses);
        Assert.InRange(looked, 1, (int)((time.GetUtcNow() - started) / SignInThrottle.GlobalSpacing) + 1);

        // Once five minutes pass without a wrong password, sign-in is as before.
        time.Advance(SignInThrottle.FailureWindow);
        Assert.Equal(SignInThrottle.Verdict.Right, throttle.Check(Fresh(), Password, Password, out _));
        Assert.Equal(SignInThrottle.Verdict.Right, throttle.Check(Fresh(), Password, Password, out _));
        Assert.Single(log, l => l.Contains("from all addresses together", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SigningOut_ClosesThatSessionsWaterfall_AndAPasswordChange_ClosesThemAll()
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start));
        await using var pipeline = r.Host.CreatePipeline(AudioSource.Parse(r.Host.Config.Audio), r.Host.Config);
        r.Page.Attach(pipeline);
        string first = await CookieAsync(r);
        string second = await CookieAsync(r);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // only against a hang
        async Task<System.Net.WebSockets.ClientWebSocket> OpenAsync(string? cookie, string? basic = null)
        {
            var socket = new System.Net.WebSockets.ClientWebSocket();
            if (cookie is not null)
            {
                socket.Options.SetRequestHeader("Cookie", cookie);
            }
            if (basic is not null)
            {
                socket.Options.SetRequestHeader("Authorization", Basic(basic));
            }
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{r.Port}/waterfall/ws"), guard.Token);
            return socket;
        }
        async Task ClosedAsync(System.Net.WebSockets.ClientWebSocket socket)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while ((await socket.ReceiveAsync(buffer, guard.Token)).MessageType != System.Net.WebSockets.WebSocketMessageType.Close)
                {
                }
            }
            catch (System.Net.WebSockets.WebSocketException)
            {
                // The connection dropped, which is what closing it does.
            }
            Assert.False(guard.IsCancellationRequested);
        }

        using var a = await OpenAsync(first);
        using var b = await OpenAsync(second);
        using var script = await OpenAsync(null, Password);
        Assert.Equal(3, r.Page.OpenSockets);

        Assert.Equal(HttpStatusCode.OK, (await r.SendAsync(Request(HttpMethod.Post, "logout", first, new { }))).StatusCode);
        Assert.Equal(2, r.Page.OpenSockets); // the other session's and the script's are still open
        await ClosedAsync(a);

        var form = new
        {
            audio = "wav:/nonexistent.wav", type = "linBpq", host = "127.0.0.1", port = 8011, login = "Q0CAST", password = "", command = "BBS",
            pagePassword = "brand-new-9K", currentPagePassword = Password,
        };
        Assert.Equal(HttpStatusCode.OK, (await r.SendAsync(Request(HttpMethod.Post, "api/settings", second, form))).StatusCode);
        Assert.Equal(0, r.Page.OpenSockets);
        await ClosedAsync(b);
        await ClosedAsync(script);
    }

    [Fact]
    public async Task SignInAndSignOut_FromAnotherSite_AreRefused()
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start));
        string cookie = await CookieAsync(r);

        var foreignIn = await SignInAsync(r, Password, origin: "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, foreignIn.StatusCode);
        Assert.Null(SetCookie(foreignIn));
        var foreignForm = new HttpRequestMessage(HttpMethod.Post, "login") { Content = new FormUrlEncodedContent([new("password", Password)]) };
        foreignForm.Headers.Add("Origin", "null");
        Assert.Equal(HttpStatusCode.Forbidden, (await r.SendAsync(foreignForm)).StatusCode);

        var foreignOut = await r.SendAsync(Request(HttpMethod.Post, "logout", cookie, new { }, "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, foreignOut.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(r, "api/status", cookie)).StatusCode);

        var signOut = await r.SendAsync(Request(HttpMethod.Post, "logout", cookie, new { }, r.Http.BaseAddress!.GetLeftPart(UriPartial.Authority)));
        Assert.Equal(HttpStatusCode.OK, signOut.StatusCode);
        Assert.Contains("Max-Age=0", SetCookie(signOut), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(r, "api/status", cookie)).StatusCode);
        Assert.Contains(r.Log, l => l == "web: signed out from 127.0.0.1");
    }

    [Theory]
    [InlineData("GET", "api/status")]
    [InlineData("GET", "api/settings")]
    [InlineData("GET", "api/mail")]
    [InlineData("GET", "api/mail/0000000000000001")]
    [InlineData("GET", "api/slots/1760011200000/frames")]
    [InlineData("POST", "api/settings")]
    [InlineData("POST", "api/mail/resend")]
    public async Task Api_WithoutASession_Is401AsJson(string method, string url)
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start));

        var answer = await r.SendAsync(Request(new HttpMethod(method), url, json: method == "POST" ? new { id = "1" } : null));

        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
        Assert.Equal("application/json", answer.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Sign in", await ErrorAsync(answer), StringComparison.Ordinal);
        Assert.Equal(Password, ReceiverConfig.Load(r.ConfigPath).Web.Password);
    }

    [Fact]
    public async Task Password_IsNeverLoggedOrSentBack()
    {
        using var dir = new TempDirectory();
        var time = new FakeTimeProvider(Start);
        await using var r = await StartAsync(dir.Path, time);

        string cookie = await CookieAsync(r, remember: true);
        await SignInAsync(r, Wrong);
        await r.SendAsync(new HttpRequestMessage(HttpMethod.Post, "login") { Content = new FormUrlEncodedContent([new("password", Wrong)]) });
        var basic = Request(HttpMethod.Get, "/");
        basic.Headers.TryAddWithoutValidation("Authorization", Basic(Wrong));
        await r.SendAsync(basic);
        await GetAsync(r, "api/settings", cookie);
        await GetAsync(r, "/", cookie);
        for (int i = 0; i < SignInThrottle.MaxFailures; i++)
        {
            await SignInAsync(r, Wrong);
        }

        Assert.Contains(r.Log, l => l.Contains("wrong passwords from", StringComparison.Ordinal));
        foreach (string said in r.Log.Concat(r.Answers))
        {
            Assert.DoesNotContain(Password, said, StringComparison.Ordinal);
            Assert.DoesNotContain(Wrong, said, StringComparison.Ordinal);
            Assert.DoesNotMatch("[^\\x00-\\x7F]", said);
        }
    }

    [Fact]
    public async Task WithoutAPassword_OnThisMachine_NoSignInIsAsked()
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start), password: "");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(r, "api/status")).StatusCode);
        var settings = await (await GetAsync(r, "api/settings")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(settings.GetProperty("web").GetProperty("passwordSet").GetBoolean());
        Assert.DoesNotContain("name=\"password\"", await (await GetAsync(r, "/")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private const string ChromeAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
    private const string FirefoxAgent = "Mozilla/5.0 (X11; Linux x86_64; rv:143.0) Gecko/20100101 Firefox/143.0";

    /// <summary>
    /// The headers each sends, as captured, to a page on a plain-HTTP network address. A browser
    /// sends no Sec-Fetch-* there: it only sends those to HTTPS and to localhost.
    /// </summary>
    public static TheoryData<string, string, string, (string, string)[]> Clients() => new()
    {
        { "Chrome opening the page", "GET", "/", [("User-Agent", ChromeAgent),
            ("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7"),
            ("Accept-Language", "en-GB,en;q=0.9"), ("Upgrade-Insecure-Requests", "1")] },
        { "Firefox opening the page", "GET", "/", [("User-Agent", FirefoxAgent),
            ("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"),
            ("Accept-Language", "en-GB,en;q=0.5"), ("Upgrade-Insecure-Requests", "1"), ("Priority", "u=0, i")] },
        { "Firefox opening the waterfall", "GET", "/waterfall/", [("User-Agent", FirefoxAgent),
            ("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")] },
        { "the page's own poll", "GET", "api/status", [("User-Agent", ChromeAgent), ("Accept", "*/*"), ("Referer", "http://10.45.0.235:8130/")] },
        { "the page's own post", "POST", "api/mail/resend", [("User-Agent", FirefoxAgent), ("Accept", "*/*")] },
        { "the favicon", "GET", "/favicon.ico", [("User-Agent", ChromeAgent),
            ("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8"), ("Referer", "http://10.45.0.235:8130/")] },
        { "the waterfall's socket", "GET", "/waterfall/ws", [("User-Agent", FirefoxAgent), ("Accept", "*/*"),
            ("Connection", "keep-alive, Upgrade"), ("Upgrade", "websocket"), ("Sec-WebSocket-Version", "13"), ("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==")] },
    };

    public static TheoryData<string, (string, string)[]> Scripts() => new()
    {
        { "curl", [("User-Agent", "curl/8.14.1"), ("Accept", "*/*")] },
        { "wget", [("User-Agent", "Wget/1.25.0"), ("Accept", "*/*"), ("Accept-Encoding", "identity")] },
        { "python-requests", [("User-Agent", "python-requests/2.32.3"), ("Accept", "*/*"), ("Accept-Encoding", "gzip, deflate"), ("Connection", "keep-alive")] },
    };

    private static HttpRequestMessage WithHeaders(string method, string url, (string Name, string Value)[] headers, string? basic = null)
    {
        var request = Request(new HttpMethod(method), url, json: method == "POST" ? new { id = "1" } : null);
        foreach (var (name, value) in headers)
        {
            if (name is "Connection" or "Upgrade")
            {
                continue; // HttpClient sets these itself; the WebSocket's are below
            }
            Assert.True(request.Headers.TryAddWithoutValidation(name, value), name);
        }
        if (headers.Any(h => h.Name == "Upgrade"))
        {
            request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        }
        if (basic is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", Basic(basic));
        }
        return request;
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task Browser_OverPlainHttp_WithoutSecFetch_IsNeverAskedForBasic_AndItsBasicIsNotCounted(string who, string method, string url, (string, string)[] headers)
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start));
        Assert.DoesNotContain(headers, h => h.Item1.StartsWith("Sec-Fetch", StringComparison.Ordinal));

        var answer = await r.SendAsync(WithHeaders(method, url, headers));
        Assert.Empty(answer.Headers.WwwAuthenticate);
        string body = await answer.Content.ReadAsStringAsync();
        bool page = method == "GET" && !url.StartsWith("api/", StringComparison.Ordinal) && !url.EndsWith("/ws", StringComparison.Ordinal);
        if (page)
        {
            // Pages, the favicon among them, get the sign-in page: harmless where an image was wanted.
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Contains("name=\"password\"", body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
        }

        // A Basic login it remembers, right or wrong, neither lets it in nor counts against it.
        Assert.Equal(HttpStatusCode.Unauthorized == answer.StatusCode ? HttpStatusCode.Unauthorized : HttpStatusCode.OK,
            (await r.SendAsync(WithHeaders(method, url, headers, Password))).StatusCode);
        for (int i = 0; i < 2 * SignInThrottle.MaxFailures; i++)
        {
            var wrong = await r.SendAsync(WithHeaders(method, url, headers, Wrong));
            Assert.Empty(wrong.Headers.WwwAuthenticate);
            Assert.DoesNotContain("not right", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.DoesNotContain(r.Log, l => l.Contains("wrong passwords", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(r, Password)).StatusCode);
        _ = who;
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task Script_IsAskedForBasic_AndItsBasicWorksAndIsCounted(string who, (string, string)[] headers)
    {
        using var dir = new TempDirectory();
        await using var r = await StartAsync(dir.Path, new FakeTimeProvider(Start));

        foreach (string url in new[] { "/", "api/status" })
        {
            var asked = await r.SendAsync(WithHeaders("GET", url, headers));
            Assert.Equal(HttpStatusCode.Unauthorized, asked.StatusCode);
            Assert.Contains("Basic", asked.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(HttpStatusCode.OK, (await r.SendAsync(WithHeaders("GET", "api/mail", headers, Password))).StatusCode);

        for (int i = 0; i < SignInThrottle.MaxFailures; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await r.SendAsync(WithHeaders("GET", "api/status", headers, Wrong))).StatusCode);
        }
        Assert.Equal((HttpStatusCode)429, (await r.SendAsync(WithHeaders("GET", "api/status", headers, Password))).StatusCode);
        Assert.Single(r.Log, l => l.Contains("wrong passwords", StringComparison.Ordinal));
        _ = who;
    }

    [Theory]
    [InlineData(null, ChromeAgent, "*/*", true)]
    [InlineData(null, "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1", null, true)]
    [InlineData(null, null, "text/html,*/*;q=0.8", true)]
    [InlineData("navigate", null, null, true)]
    [InlineData(null, "curl/8.14.1", "*/*", false)]
    [InlineData(null, "Wget/1.25.0", "*/*", false)]
    [InlineData(null, "python-requests/2.32.3", "*/*", false)]
    [InlineData(null, null, null, false)]
    [InlineData(null, null, "application/json", false)]
    public void IsBrowser_ByItsHeaders(string? secFetchMode, string? userAgent, string? accept, bool browser)
    {
        Assert.Equal(browser, StatusPage.IsBrowser(secFetchMode, userAgent, accept));
    }
}
