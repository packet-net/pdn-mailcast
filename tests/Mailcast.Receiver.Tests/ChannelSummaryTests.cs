using Packet.Mailcast.Feedback;
using Mailcast.Receiver.Feedback;

namespace Mailcast.Receiver.Tests;

/// <summary>The Channel tile's measurement and the web SDR's place, as the daily report's fields.</summary>
public class ChannelSummaryTests
{
    private static ChannelReport Report(params ChannelMode[] modes) => new()
    {
        Slot = new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero),
        Enough = true,
        Modes = modes,
        DelaySpreadMs = 0.35,
        DopplerSpreadHz = 0.21,
        VirtualHeightKm = 287,
    };

    [Fact]
    public void NamedHops_GiveThe2FModeAgainstTheFirst()
    {
        var report = Report(
            new ChannelMode { Label = "1F", DelayMs = 0, PowerDb = -1.5 },
            new ChannelMode { Label = "3F", DelayMs = 3.9, PowerDb = -25 },
            new ChannelMode { Label = "2F", DelayMs = 1.94, PowerDb = -18.1 });

        Assert.Equal(new ReportChannel(3, 1.94, -18.1 + 1.5, 0.35, 0.21, 287, 'b'), ChannelSummary.From(report));
    }

    [Fact]
    public void UnnamedHops_GiveTheSecondMode_AndOneModeGivesNone()
    {
        var two = Report(new ChannelMode { DelayMs = 0, PowerDb = 0 }, new ChannelMode { DelayMs = 2.1, PowerDb = -14 });
        Assert.Equal(new ReportChannel(2, 2.1, -14, 0.35, 0.21, 287, 'b'), ChannelSummary.From(two));

        var one = Report(new ChannelMode { Label = "1F", DelayMs = 0, PowerDb = 0 }) with { VirtualHeightKm = null, Basis = "probe" };
        Assert.Equal(new ReportChannel(1, null, null, 0.35, 0.21, null, 'p'), ChannelSummary.From(one));
    }

    [Fact]
    public void TooLittleToMeasure_GivesNoChannel()
    {
        Assert.Null(ChannelSummary.From(null));
        Assert.Null(ChannelSummary.From(Report(new ChannelMode()) with { Enough = false }));
        Assert.Null(ChannelSummary.From(Report()));
    }

    [Fact]
    public void Locator_IsTheWebSdrsOwn_OrItsPositionAsSixCharacters()
    {
        Assert.Null(ChannelSummary.Locator(null));
        Assert.Equal("IO90au", ChannelSummary.Locator(new GroundPlace(50.9, -1.95, "IO90au")));
        Assert.Equal("IO91wm", ChannelSummary.Locator(new GroundPlace(51.5, -0.12, "51.50,-0.12")));
    }
}
