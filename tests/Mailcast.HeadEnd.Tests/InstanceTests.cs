using System.Net;
using System.Net.Sockets;
using Mailcast.HeadEnd.Service;
using Mailcast.HeadEnd.Status;

namespace Mailcast.HeadEnd.Tests;

/// <summary>More than one head end on a machine: pdn-mailcast-headend@NAME beside the plain service.</summary>
[Collection(KeyPathTests.Name)]
public class InstanceTests
{
    private static string Example => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "headend.example.json"));

    [Theory]
    [InlineData("/etc/pdn-mailcast-headend/headend.json", null, "/var/lib/pdn-mailcast-headend")]
    [InlineData(null, null, "/var/lib/pdn-mailcast-headend")]
    [InlineData("/etc/pdn-mailcast-headend/qo100.json", null, "/var/lib/pdn-mailcast-headend-qo100")]
    [InlineData("/etc/pdn-mailcast-headend/qo100.json", "/var/lib/pdn-mailcast-headend-qo100", "/var/lib/pdn-mailcast-headend-qo100")]
    [InlineData("/home/tf/other.json", "/var/lib/somewhere", "/var/lib/somewhere")]
    [InlineData("/etc/pdn-mailcast-headend/headend.json", "", "/var/lib/pdn-mailcast-headend")]
    // pdn-mailcast-headend@headend: still the plain directory, so the lock or the read-only check refuses it.
    [InlineData("/etc/pdn-mailcast-headend/headend.json", "/var/lib/pdn-mailcast-headend-headend", "/var/lib/pdn-mailcast-headend")]
    public void StateDirectoryFor_IsSystemdsOrNamedAfterTheFile(string? configPath, string? systemd, string expected) =>
        Assert.Equal(expected, HeadEndConfig.StateDirectoryFor(configPath, systemd));

    [Fact]
    public void TheExample_LeavesTheDirectoriesOut_SoACopyForASecondHeadEndHasItsOwn()
    {
        var config = HeadEndConfig.Parse(ExampleConfig.Filled(Example)) with { DefaultStateDirectory = "/var/lib/pdn-mailcast-headend-qo100" };
        Assert.Null(config.StateDirectoryGiven);
        Assert.Equal("/var/lib/pdn-mailcast-headend-qo100", config.StateDirectory);
        Assert.Equal("/var/lib/pdn-mailcast-headend-qo100/drop", config.DropDirectory);

        // headend.json as the package installs it is where it always was.
        var plain = HeadEndConfig.Parse(ExampleConfig.Filled(Example));
        Assert.Equal("/var/lib/pdn-mailcast-headend", plain.StateDirectory);
        Assert.Equal("/var/lib/pdn-mailcast-headend/drop", plain.DropDirectory);
    }

    [Fact]
    public void GivenDirectories_AreUsed_AndAnEmptyDropDirectoryTurnsTheDropOff()
    {
        const string Minimal = """{"callsign": "M0LTE", "station": {"apiKey": "k"}, "stateDirectory": "/srv/he", "intake": {"dropDirectory": "/srv/drop"}}""";
        var config = HeadEndConfig.Parse(Minimal) with { DefaultStateDirectory = "/var/lib/pdn-mailcast-headend-qo100" };
        Assert.Equal("/srv/he", config.StateDirectory);
        Assert.Equal("/srv/drop", config.DropDirectory);

        var off = HeadEndConfig.Parse("""{"callsign": "M0LTE", "station": {"apiKey": "k"}, "intake": {"dropDirectory": ""}}""");
        Assert.Null(off.DropDirectory);
        Assert.Equal("state: /var/lib/pdn-mailcast-headend, file drop off", Program.DescribeDirectories(off));
    }

    [Fact]
    public void LoadConfig_NamesTheStateDirectoryAfterTheFile()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "qo100.json");
        File.WriteAllText(path, ExampleConfig.Filled(Example));
        string? old = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", null);
            Assert.Equal("/var/lib/pdn-mailcast-headend-qo100", Program.LoadConfig(path, offline: false, pathGiven: true, out _).StateDirectory);
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", "/var/lib/pdn-mailcast-headend-other");
            Assert.Equal("/var/lib/pdn-mailcast-headend-other", Program.LoadConfig(path, offline: false, pathGiven: true, out _).StateDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STATE_DIRECTORY", old);
        }
    }

    [Fact]
    public void ThePlainConfig_IsRefusedUnderTheTemplate_InOneLine()
    {
        const string Plain = "/etc/pdn-mailcast-headend/headend.json";
        Assert.Null(Program.PlainConfigUnderTemplate(Plain, null));
        Assert.Null(Program.PlainConfigUnderTemplate(Plain, "/var/lib/pdn-mailcast-headend"));
        Assert.Null(Program.PlainConfigUnderTemplate("/etc/pdn-mailcast-headend/qo100.json", "/var/lib/pdn-mailcast-headend-qo100"));
        Assert.Equal(
            "/etc/pdn-mailcast-headend/headend.json is the plain service's config; run it as pdn-mailcast-headend, and give a second head end a NAME.json of its own",
            Program.PlainConfigUnderTemplate(Plain, "/var/lib/pdn-mailcast-headend-headend"));
    }

    [Fact]
    public void DirectoryLock_ASecondHeadEndCannotTakeTheSameDirectory_UntilTheFirstLetsGo()
    {
        using var dir = new TempDirectory();
        string state = Path.Combine(dir.Path, "state");
        using (var first = DirectoryLock.Take(state, "\"stateDirectory\"", out string? none))
        {
            Assert.NotNull(first);
            Assert.Null(none);
            Assert.True(File.Exists(Path.Combine(state, DirectoryLock.FileName)));

            Assert.Null(DirectoryLock.Take(state, "\"stateDirectory\"", out string? problem));
            Assert.Equal($"another head end is already using {state}; give this one its own \"stateDirectory\" (leave it out under pdn-mailcast-headend@NAME)", problem);
        }
        using var again = DirectoryLock.Take(state, "\"stateDirectory\"", out string? later);
        Assert.NotNull(again);
        Assert.Null(later);
    }

    [Fact]
    public void DirectoryLock_ADirectoryItCannotWrite_SaysSoInOneLine()
    {
        using var dir = new TempDirectory();
        string file = Path.Combine(dir.Path, "a-file");
        File.WriteAllText(file, "");
        Assert.Null(DirectoryLock.Take(file, "\"stateDirectory\"", out string? problem));
        Assert.StartsWith($"cannot write in {file} (\"stateDirectory\"): ", problem, StringComparison.Ordinal);
        Assert.EndsWith("; leave \"stateDirectory\" out of the configuration to use the service's own", problem, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', problem!);
    }

    [Fact]
    public async Task TheService_StopsAtStartUp_WhenAnotherHeadEndHasItsStateDirectory()
    {
        using var dir = new TempDirectory();
        string state = Path.Combine(dir.Path, "state");
        string config = Path.Combine(dir.Path, "qo100.json");
        File.WriteAllText(config, ExampleConfig.Filled(Example).Replace("\"destination\": \"MCAST\",", $"\"destination\": \"MCAST\", \"stateDirectory\": \"{state}\",", StringComparison.Ordinal));
        string oldKey = Program.KeyPath;
        var oldErr = Console.Error;
        var oldOut = Console.Out;
        var err = new StringWriter();
        try
        {
            Program.KeyPath = Path.Combine(dir.Path, "signing-key.pem");
            Console.SetError(err);
            Console.SetOut(TextWriter.Null);
            Assert.Equal(0, await Program.MainAsync(["--make-key"]));
            using var other = DirectoryLock.Take(state, "\"stateDirectory\"", out _);
            Assert.Equal(2, await Program.MainAsync(["--config", config]));
        }
        finally
        {
            Console.SetError(oldErr);
            Console.SetOut(oldOut);
            Program.KeyPath = oldKey;
        }
        Assert.Equal($"pdn-mailcast-headend: another head end is already using {state}; give this one its own \"stateDirectory\" (leave it out under pdn-mailcast-headend@NAME)", err.ToString().Trim());
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task StatusListener_APortAlreadyTaken_IsToldApart_AndTheLineNamesTheSetting()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;
        await using var server = new StatusServer("127.0.0.1", port, new StatusStore(null, TimeProvider.System));
        var e = Assert.Throws<HttpListenerException>(server.Start);
        Assert.True(Program.StatusPortTaken(e), $"ErrorCode {e.ErrorCode}, NativeErrorCode {e.NativeErrorCode}: {e.Message}");

        var config = HeadEndConfig.Parse($$$"""{"callsign": "M0LTE", "station": {"apiKey": "k"}, "status": {"port": {{{port}}}}}""");
        Assert.Equal($"pdn-mailcast-headend: the status port {port} is taken, perhaps by another head end; give this one its own \"status\".\"port\"", Program.StatusPortTakenProblem(config));
    }

    [Fact]
    public async Task RunNow_AsksTheHeadEndItsConfigNames()
    {
        using var dir = new TempDirectory();
        int hfPort = FreePort();
        int qo100Port = FreePort();
        var asked = new List<string>();
        var status = new StatusStore(null, TimeProvider.System);
        RunNowAnswer Answer(string name) { asked.Add(name); return new RunNowAnswer(true, DateTimeOffset.UnixEpoch, null); }
        await using var hf = new StatusServer("127.0.0.1", hfPort, status, _ => Answer("hf"));
        await using var qo100 = new StatusServer("127.0.0.1", qo100Port, status, _ => Answer("qo100"));
        hf.Start();
        qo100.Start();
        string Write(string name, int port)
        {
            string path = Path.Combine(dir.Path, name + ".json");
            File.WriteAllText(path, ExampleConfig.Filled(Example).Replace("\"port\": 8216", $"\"port\": {port}", StringComparison.Ordinal));
            return path;
        }
        string hfConfig = Write("headend", hfPort);
        string qo100Config = Write("qo100", qo100Port);

        var oldOut = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            Assert.Equal(0, await Program.MainAsync(["--run-now", "--config", qo100Config]));
            Assert.Equal(["qo100"], asked);
            Assert.Equal(0, await Program.MainAsync(["--run-now", "--config", hfConfig]));
            Assert.Equal(["qo100", "hf"], asked);
        }
        finally
        {
            Console.SetOut(oldOut);
        }
    }
}

/// <summary>Tests that change <see cref="Program.KeyPath"/> or the console run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class KeyPathTests
{
    public const string Name = "Program.KeyPath";
}
