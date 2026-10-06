using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Mailcast.Receiver.Updates;
using Mailcast.Receiver.Web;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The status page's "an update is ready": the apt repository's index read, the newest receiver
/// for this architecture found and compared as apt would, the right commands for how it was
/// installed, silence when it cannot check, and the cadence. No network: the repository is a
/// fake handler serving a copy of the real index.
/// </summary>
public class UpdateCheckTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 14, 20, 0, TimeSpan.Zero);

    /// <summary>packet-net's real index as it was on 6 October 2026, with pdn-mailcast-receiver 0.5.2 for amd64, arm64 and armhf.</summary>
    private static string RealIndex() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Updates", "Packages-2026-10-06"));

    private static string Stanza(string package, string version, string arch) => $"""
        Package: {package}
        Version: {version}
        Architecture: {arch}
        Filename: pool/{package}_{version}_{arch}.deb
        Description: something
         a continuation line: Version: 99.0
        """ + "\n\n";

    [Theory]
    [InlineData("0.6.0", "0.5.2", 1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("0.6.0", "0.6.0", 0)]
    [InlineData("0.06.0", "0.6.0", 0)]
    [InlineData("0.6.1", "0.6.0", 1)]
    [InlineData("1.0~rc1", "1.0", -1)]
    [InlineData("1.0~rc1", "1.0~rc2", -1)]
    [InlineData("1.0", "1.0+b1", -1)]
    [InlineData("1.0a", "1.0", 1)]
    [InlineData("1.0a", "1.0+", -1)]
    [InlineData("1:0.1", "2.0", 1)]
    [InlineData("0.6.0-1", "0.6.0", 1)]
    [InlineData("0.6.0-2", "0.6.0-10", -1)]
    [InlineData("0.7.0-rc1", "0.6.9", 1)]
    public void Compare_OrdersVersionsAsDpkgDoes(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(DebianVersion.Compare(a, b)));
        Assert.Equal(-sign, Math.Sign(DebianVersion.Compare(b, a)));
    }

    [Fact]
    public void WithoutBuild_DropsALocalBuildsCommit_SoTheSameReleaseIsNotOlder()
    {
        Assert.Equal("0.6.0", DebianVersion.WithoutBuild("0.6.0+3f2a9c1e5b7d"));
        Assert.Equal("0.7.0-rc1", DebianVersion.WithoutBuild(" 0.7.0-rc1+git.abc "));
        Assert.Equal("0.6.0", DebianVersion.WithoutBuild("0.6.0"));
        // Compared as it stands, "+..." would count as newer than the release itself.
        Assert.True(DebianVersion.Compare("0.6.0+abc", "0.6.0") > 0);
        Assert.Equal(0, DebianVersion.Compare("0.6.0", DebianVersion.WithoutBuild("0.6.0+abc")));
        Assert.Equal("0.6.0", new UpdateCheck(TimeProvider.System, _ => { }, "0.6.0+deadbeef", "amd64").Current);
    }

    [Theory]
    [InlineData("0.6.0", true)]
    [InlineData("1:2.0-1", true)]
    [InlineData("1.0~rc1+b2", true)]
    [InlineData("0.0.0-pr37.12", true)]
    [InlineData("", false)]
    [InlineData("v0.6.0", false)]
    [InlineData("0.6.0-", false)]
    [InlineData("0 6", false)]
    [InlineData("x:1.0", false)]
    public void IsValid_TakesWhatDpkgTakes(string version, bool valid) =>
        Assert.Equal(valid, DebianVersion.IsValid(version));

    [Fact]
    public void Parse_TheRealIndex_FindsTheReceiverForEachArchitecture()
    {
        var packages = AptPackages.Parse(RealIndex());
        Assert.True(packages.Count >= 60);
        foreach (string arch in new[] { "amd64", "arm64", "armhf" })
        {
            var newest = AptPackages.Newest(packages, AptPackages.ReceiverPackage, arch);
            Assert.NotNull(newest);
            Assert.Equal("0.5.2", newest.Version);
            Assert.Equal(arch, newest.Architecture);
            Assert.Equal($"pool/pdn-mailcast-receiver_0.5.2_{arch}.deb", newest.Filename);
        }
        Assert.Null(AptPackages.Newest(packages, AptPackages.ReceiverPackage, "i386"));
        Assert.Equal("pdn-mailcast-headend", AptPackages.Newest(packages, "pdn-mailcast-headend", "amd64")!.Name);
    }

    [Fact]
    public void Newest_IsPerArchitecture_AndByDebianOrder_NotByOrderInTheFile()
    {
        string index = Stanza("pdn-mailcast-receiver", "0.10.0", "amd64")
            + Stanza("pdn-mailcast-receiver", "0.9.0", "amd64")
            + Stanza("pdn-mailcast-receiver", "0.7.0", "arm64")
            + Stanza("pdn-mailcast-headend", "0.11.0", "arm64")
            + Stanza("pdn-mailcast-receiver", "0.6.0", "armhf")
            + Stanza("pdn-mailcast-receiver", "0.6.0", "armhf").Replace("Version: 0.6.0", "Version: not-a-version", StringComparison.Ordinal);
        var packages = AptPackages.Parse(index.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal("0.10.0", AptPackages.Newest(packages, AptPackages.ReceiverPackage, "amd64")!.Version);
        Assert.Equal("0.7.0", AptPackages.Newest(packages, AptPackages.ReceiverPackage, "arm64")!.Version);
        Assert.Equal("0.6.0", AptPackages.Newest(packages, AptPackages.ReceiverPackage, "armhf")!.Version);
        Assert.Null(AptPackages.Newest(packages, AptPackages.ReceiverPackage, "riscv64"));
    }

    [Fact]
    public void Parse_SomethingElse_Throws()
    {
        Assert.Throws<FormatException>(() => AptPackages.Parse("<html><body>Not Found</body></html>"));
        Assert.Throws<FormatException>(() => AptPackages.Parse(""));
    }

    [Fact]
    public void Architecture_IsDebiansName()
    {
        Assert.Equal("amd64", AptPackages.Architecture(System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("arm64", AptPackages.Architecture(System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Equal("armhf", AptPackages.Architecture(System.Runtime.InteropServices.Architecture.Arm));
        Assert.Null(AptPackages.Architecture(System.Runtime.InteropServices.Architecture.X86));
    }

    [Theory]
    // What the README tells you to create, in /etc/apt/sources.list.d/packet-net.list.
    [InlineData("deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./\n", true)]
    [InlineData("# deb [signed-by=/usr/share/keyrings/packet-net.gpg] https://packet-net.github.io/apt ./\n", false)]
    [InlineData("deb http://deb.debian.org/debian trixie main\n", false)]
    [InlineData("Types: deb\nURIs: https://packet-net.github.io/apt\nSuites: ./\nSigned-By: /usr/share/keyrings/packet-net.gpg\n", true)]
    [InlineData("Types: deb\nURIs: https://packet-net.github.io/apt\nSuites: ./\nEnabled: no\n", false)]
    [InlineData("Types: deb\nURIs: http://deb.debian.org/debian\nSuites: trixie\nEnabled: no\n\nTypes: deb\nURIs: https://packet-net.github.io/apt\nSuites: ./\n", true)]
    public void NamesRepo_FindsALiveEntryForPacketNetsRepo(string file, bool expected) =>
        Assert.Equal(expected, AptSources.NamesRepo(["deb http://deb.debian.org/debian trixie main\n", file]));

    [Fact]
    public async Task Newer_FromTheAptRepo_GivesTheAptCommand()
    {
        var repo = new FakeRepo(_ => Ok(RealIndex()));
        var log = new List<string>();
        using var check = new UpdateCheck(new FakeTimeProvider(Start), log.Add, "0.5.0", "arm64", repo, fromAptRepo: () => true);

        await check.CheckOnceAsync(CancellationToken.None);

        var view = View(check);
        Assert.True(view.GetProperty("newer").GetBoolean());
        Assert.Equal("0.5.2", view.GetProperty("latest").GetString());
        Assert.Equal("0.5.0", view.GetProperty("current").GetString());
        Assert.Equal(Start, view.GetProperty("checkedAt").GetDateTimeOffset());
        Assert.Equal("https://github.com/packet-net/pdn-mailcast/releases/tag/v0.5.2", view.GetProperty("release").GetString());
        Assert.Equal("sudo apt update && sudo apt install --only-upgrade pdn-mailcast-receiver", view.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("download").ValueKind);
        Assert.Equal("update: version 0.5.2 is available (you have 0.5.0); the status page says how to upgrade", Assert.Single(log));

        // Said once a version, not at every check.
        await check.CheckOnceAsync(CancellationToken.None);
        Assert.Single(log);
    }

    [Fact]
    public async Task Newer_InstalledByHand_GivesTheDebForThisArchitecture()
    {
        bool aptRepo = true;
        var repo = new FakeRepo(_ => Ok(RealIndex()));
        using var check = new UpdateCheck(new FakeTimeProvider(Start), _ => { }, "0.5.1", "armhf", repo, fromAptRepo: () => aptRepo);
        aptRepo = false;

        await check.CheckOnceAsync(CancellationToken.None);

        var view = View(check);
        Assert.True(view.GetProperty("newer").GetBoolean());
        Assert.False(view.GetProperty("aptRepo").GetBoolean());
        Assert.Equal("https://github.com/packet-net/pdn-mailcast/releases/download/v0.5.2/pdn-mailcast-receiver_0.5.2_armhf.deb", view.GetProperty("download").GetString());
        Assert.Equal("sudo apt install ./pdn-mailcast-receiver_0.5.2_armhf.deb", view.GetProperty("command").GetString());
    }

    [Theory]
    [InlineData("0.5.2")]
    [InlineData("0.5.2+3f2a9c1")]
    [InlineData("0.6.0")]
    public async Task UpToDate_OrNewer_ShowsNothing(string current)
    {
        var log = new List<string>();
        using var check = new UpdateCheck(new FakeTimeProvider(Start), log.Add, current, "amd64", new FakeRepo(_ => Ok(RealIndex())), () => true);

        await check.CheckOnceAsync(CancellationToken.None);

        var view = View(check);
        Assert.False(view.GetProperty("newer").GetBoolean());
        Assert.Equal("0.5.2", view.GetProperty("latest").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("command").ValueKind);
        Assert.Empty(log);
    }

    public static TheoryData<string> Failures => ["offline", "dns", "404", "html", "timeout", "noArch"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailure_ShowsNothing_AndIsLoggedOnceForItsKind(string failure)
    {
        var time = new FakeTimeProvider(Start);
        var asked = Channel.CreateUnbounded<bool>();
        var repo = new FakeRepo(async (request, cancellation) =>
        {
            asked.Writer.TryWrite(true);
            switch (failure)
            {
                case "offline": throw new HttpRequestException("Network is unreachable (packet-net.github.io:443)");
                case "dns": throw new HttpRequestException("Name or service not known (packet-net.github.io:443)");
                case "404": return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") };
                case "html": return Ok("<!doctype html><html><body>Captive portal: sign in</body></html>");
                case "timeout":
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                    throw new InvalidOperationException("not reached");
                default: return Ok(Stanza("pdn-mailcast-receiver", "0.9.0", "amd64"));
            }
        });
        var log = new List<string>();
        using var check = new UpdateCheck(time, line => { lock (log) { log.Add(line); } }, "0.5.0", "arm64", repo, () => true);

        for (int i = 0; i < 3; i++)
        {
            Task once = check.CheckOnceAsync(CancellationToken.None);
            await asked.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (failure == "timeout")
            {
                Assert.False(once.IsCompleted);
                time.Advance(UpdateCheck.Timeout);
            }
            await once.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var view = View(check);
        Assert.False(view.GetProperty("newer").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("latest").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("checkedAt").ValueKind);
        string line = Assert.Single(log);
        Assert.StartsWith("update: cannot check for a newer version: ", line, StringComparison.Ordinal);
        Assert.True(line.All(char.IsAscii), line);
    }

    [Fact]
    public async Task AFailureAfterAFinding_KeepsTheFinding_AndANewOutageIsLoggedAgain()
    {
        bool up = true;
        var repo = new FakeRepo(_ => up ? Ok(RealIndex()) : throw new HttpRequestException("Network is unreachable"));
        var log = new List<string>();
        using var check = new UpdateCheck(new FakeTimeProvider(Start), log.Add, "0.5.0", "amd64", repo, () => true);

        await check.CheckOnceAsync(CancellationToken.None);
        up = false;
        await check.CheckOnceAsync(CancellationToken.None);
        await check.CheckOnceAsync(CancellationToken.None);
        Assert.Equal("0.5.2", View(check).GetProperty("latest").GetString());
        Assert.True(View(check).GetProperty("newer").GetBoolean());
        Assert.Equal(2, log.Count); // the finding, then the outage once

        up = true;
        await check.CheckOnceAsync(CancellationToken.None);
        up = false;
        await check.CheckOnceAsync(CancellationToken.None);
        Assert.Equal(3, log.Count);
        Assert.Equal(log[1], log[2]);
    }

    [Fact]
    public async Task Run_LooksSoonAfterStartUp_ThenEverySixHours_WithItsUserAgent()
    {
        var time = new FakeTimeProvider(Start);
        var asked = Channel.CreateUnbounded<HttpRequestMessage>();
        var repo = new FakeRepo(request =>
        {
            asked.Writer.TryWrite(request);
            return Ok(RealIndex());
        });
        using var check = new UpdateCheck(time, _ => { }, "0.6.0", "amd64", repo, () => true) { FirstDelay = TimeSpan.FromMinutes(1) };
        using var stop = new CancellationTokenSource();
        Task run = check.RunAsync(stop.Token);

        async Task<HttpRequestMessage> Next() => await asked.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        async Task Nothing()
        {
            await Task.Delay(100);
            Assert.False(asked.Reader.TryRead(out _));
        }

        await Nothing();
        time.Advance(TimeSpan.FromSeconds(59));
        await Nothing();
        time.Advance(TimeSpan.FromSeconds(1));
        var first = await Next();
        Assert.Equal(new Uri("https://packet-net.github.io/apt/Packages"), first.RequestUri);
        Assert.Equal("pdn-mailcast-receiver/0.6.0 (+https://github.com/packet-net/pdn-mailcast)", string.Join(" ", first.Headers.GetValues("User-Agent")));
        Assert.Equal(TimeSpan.FromHours(6), UpdateCheck.Every);

        for (int i = 0; i < 3; i++)
        {
            time.Advance(UpdateCheck.Every - TimeSpan.FromSeconds(1));
            await Nothing();
            time.Advance(TimeSpan.FromSeconds(1));
            await Next();
        }
        await Nothing();

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Run_ForAnArchitectureWithNoPackages_NeverAsks()
    {
        var repo = new FakeRepo(_ => throw new InvalidOperationException("asked"));
        using var check = new UpdateCheck(new FakeTimeProvider(Start), _ => { }, "0.6.0", null, repo) { FirstDelay = TimeSpan.Zero };
        await check.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task Status_HasTheUpdate()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "wav:/nonexistent.wav", StateDirectory = dir.Path, Web = new WebSettings { Port = 1 } };
        await using var host = new ReceiverHost(config, TimeProvider.System, _ => { });
        using var check = new UpdateCheck(new FakeTimeProvider(Start), _ => { }, "0.5.0", "amd64", new FakeRepo(_ => Ok(RealIndex())), () => true);
        // Never started, so it holds no port.
        var page = new StatusPage(host, null, _ => { }) { Updates = check };

        var before = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine).GetProperty("update");
        Assert.False(before.GetProperty("newer").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("latest").ValueKind);

        await check.CheckOnceAsync(CancellationToken.None);
        var update = JsonSerializer.SerializeToElement(page.Status(), ReceiverConfig.JsonLine).GetProperty("update");
        Assert.Equal("0.5.2", update.GetProperty("latest").GetString());
        Assert.Equal("0.5.0", update.GetProperty("current").GetString());
        Assert.True(update.GetProperty("newer").GetBoolean());
        Assert.Equal(Start, update.GetProperty("checkedAt").GetDateTimeOffset());

        var none = new StatusPage(host, null, _ => { });
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(none.Status(), ReceiverConfig.JsonLine).GetProperty("update").ValueKind);
    }

    [Fact]
    public void ThePage_ShowsTheBanner_FromTheStatus()
    {
        using var stream = typeof(StatusPage).Assembly.GetManifestResourceStream("Mailcast.Receiver.Web.index.html")!;
        string html = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
        Assert.Contains("id=\"update\" hidden", html, StringComparison.Ordinal);
        Assert.Contains("is available (you have", html, StringComparison.Ordinal);
        Assert.Contains("after :13 and before :58 past the hour", html, StringComparison.Ordinal);
    }

    private static JsonElement View(UpdateCheck check) => JsonSerializer.SerializeToElement(check.View(), ReceiverConfig.JsonLine);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/octet-stream") };

    /// <summary>The repository, answering each request with <c>answer</c>.</summary>
    private sealed class FakeRepo(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private int _calls;

        public FakeRepo(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((request, _) => Task.FromResult(answer(request)))
        {
        }

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return answer(request, cancellationToken);
        }
    }
}
