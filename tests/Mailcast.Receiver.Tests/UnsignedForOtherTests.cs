using System.Runtime.CompilerServices;
using Packet.Mailcast;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// Most of these tests are about something other than signing and feed a receiver unsigned
/// directories, which a real receiver does not use (issue #146): here they are used, unless a
/// test says otherwise with its own <see cref="ReceiverStoreOptions"/>.
/// </summary>
internal static class UnsignedForOtherTests
{
    [ModuleInitializer]
    internal static void Allow()
    {
        ReceiverStoreOptions.DefaultUnsignedDirectoriesAreUsed = true;
        // Nothing in the product knows a station: the tests that need one have this one, in daylight, as the config's and the store's.
        ReceiverStoreOptions.DefaultStations = StationFixture.Stations;
        ReceiverConfig.TestStations = StationFixture.Stations;
        AudioPipeline.TestSignalHz = StationFixture.SignalHz;
        AudioPipeline.TestDialHz = StationFixture.DialKHz * 1000;
    }
}

/// <summary>What the receiver does with an unsigned directory when nothing says to use it (issue #146).</summary>
public class UnsignedDirectoryRefusedTests
{
    [Fact]
    public async Task AnUnsignedDirectory_IsNotUsed_AndTheLogSaysSoOnce()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        await using var intake = new Intake(dir.Path, log.Add, new ReceiverStoreOptions { UnsignedDirectoriesAreUsed = false });
        foreach (int dayOffset in new[] { 0, 1 })
        {
            foreach (byte[] frame in Samples.Frames([Samples.Bulletin(1)], dayOffset: dayOffset))
            {
                intake.Offer(frame);
            }
            await intake.DrainAsync(CancellationToken.None);
        }
        var (newest, lastHeard) = intake.DirectoryChecks();
        Assert.Null(newest);
        Assert.Equal("unsigned, not used", lastHeard!.Check.Describe());
        var said = log.Where(l => l.Contains("no signature", StringComparison.Ordinal)).ToList();
        Assert.Single(said);
        Assert.All(said, l => Assert.All(l, c => Assert.InRange(c, ' ', '~')));
        Assert.Empty(intake.HeardSchedules);
    }

    [Fact]
    public async Task AReceiverHost_WithTheProductionRule_UsesNoUnsignedDirectory_AndKeepsTheConfigsSlots()
    {
        using var dir = new TempDirectory();
        var config = new ReceiverConfig { Audio = "wav:/nonexistent.wav", StateDirectory = dir.Path, Bbs = new BbsSettings { Port = 8011, Login = "Q0CAST", Password = "x" } };
        var log = new List<string>();
        await using var host = new ReceiverHost(config, TimeProvider.System, log.Add, refuseUnsigned: true);
        var before = host.Intake.HeardSchedules.Count;
        foreach (byte[] frame in Samples.Frames([Samples.Bulletin(1)]))
        {
            host.Intake.Offer(frame);
        }
        await host.Intake.DrainAsync(CancellationToken.None);
        Assert.Null(host.Intake.DirectoryChecks().Newest);
        Assert.Equal("unsigned, not used", host.Intake.DirectoryChecks().LastHeard!.Check.Describe());
        Assert.Equal(before, host.Intake.HeardSchedules.Count);
        Assert.Empty(host.Intake.Pending());
    }
}
