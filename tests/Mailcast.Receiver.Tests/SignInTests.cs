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
    private static async Task<Receiver> StartAsync(string dir, FakeTimeProvider time, string password = Password, bool delay = false)
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
            var page = new StatusPage(host, path, Log) { FailureDelay = delay ? SignInThrottle.FailureDelay : TimeSpan.Zero };
            try
            {
                page.Start();
                var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                };
                return new Receiver { Host = host, Page = page, Http = http, ConfigPath = path, Log = log };
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                await page.DisposeAsync();
                await host.DisposeAsync();
            }
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? cookie = null, object? json = null, string? origin = null)
    {
        var request = new HttpRequestMessage(method, url);
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

    private static Task<HttpResponseMessage> GetAsync(Receiver r, string url, string? cookie = null) =>
        r.SendAsync(Request(HttpMethod.Get, url, cookie));

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

        var page = await GetAsync(r, "/");
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
        Assert.Contains("action=\"../login\"", await (await GetAsync(r, "/waterfall/")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

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
        string signedIn = await (await GetAsync(r, "/", cookie)).Content.ReadAsStringAsync();
        Assert.Contains("api/status", signedIn, StringComparison.Ordinal);
        Assert.Contains("Sign out", signedIn, StringComparison.Ordinal);
        var settings = await (await GetAsync(r, "api/settings", cookie)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(settings.GetProperty("web").GetProperty("passwordSet").GetBoolean());
        Assert.Contains(r.Log, l => l.StartsWith("web: signed in from 127.0.0.1", StringComparison.Ordinal));
        // Once in, /login goes back to the page.
        var again = await GetAsync(r, "login", cookie);
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
        var time = new FakeTimeProvider(Start);
        await using var r = await StartAsync(dir.Path, time, delay: true);

        // The fake clock only moves when told to: the right password needs none of it.
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(r, Password)).StatusCode);

        var started = time.GetUtcNow();
        var wrong = SignInAsync(r, Wrong);
        while (!wrong.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong).StatusCode);
        Assert.True(time.GetUtcNow() - started >= SignInThrottle.FailureDelay);
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
}
