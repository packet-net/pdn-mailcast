using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

public class IntakeTests
{
    [Fact]
    public async Task Frames_RebuildBulletins_WhichWaitInTheOutboxAcrossARestart()
    {
        using var dir = new TempDirectory();
        var bulletins = new[] { Samples.Bulletin(1), Samples.Bulletin(2, bodyLines: 80), Samples.Bulletin(3) };
        var completed = new List<string>();

        await using (var intake = new Intake(dir.Path, _ => { }))
        {
            intake.BulletinCompleted += b => completed.Add(b.Bid);
            foreach (var frame in Samples.Frames(bulletins))
            {
                Assert.True(intake.Offer(frame));
            }
            await intake.DrainAsync(CancellationToken.None);
        }

        Assert.Equal(bulletins.Select(b => b.Bid).Order(), completed.Order());
        await using (var reopened = new Intake(dir.Path, _ => { }))
        {
            Assert.Equal(bulletins.OrderBy(b => b.Bid), reopened.Pending().OrderBy(b => b.Bid));
            reopened.Acknowledge("2_GB7RDG");
            Assert.Equal(2, reopened.Pending().Count);
        }
    }

    [Fact]
    public async Task OtherTraffic_IsIgnored()
    {
        using var dir = new TempDirectory();
        await using var intake = new Intake(dir.Path, _ => { });
        var payload = Samples.Frames([Samples.Bulletin(1)])[0].AsSpan(16).ToArray();

        Assert.False(intake.Offer(Ax25UiFrame.Build("G4ABC", OnAir.Destination, payload)));
        Assert.False(intake.Offer(Ax25UiFrame.Build(OnAir.Source, "ID", payload)));
        Assert.False(intake.Offer([1, 2, 3]));
        Assert.True(intake.Offer(Ax25UiFrame.Build(OnAir.Source + "-3", OnAir.Destination, payload)));
        Assert.Equal(1, intake.FramesHeard);
    }
}
