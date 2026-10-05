namespace Mailcast.Receiver.Tests;

public class ListeningWindowTests
{
    private static readonly TimeOnly Noon = new(12, 0);

    [Theory]
    [InlineData("2026-10-05T09:00:00Z", "2026-10-05T11:45:00Z", "2026-10-05T13:30:00Z")]
    [InlineData("2026-10-05T11:45:00Z", "2026-10-05T11:45:00Z", "2026-10-05T13:30:00Z")]
    [InlineData("2026-10-05T13:29:59Z", "2026-10-05T11:45:00Z", "2026-10-05T13:30:00Z")]
    [InlineData("2026-10-05T13:30:00Z", "2026-10-06T11:45:00Z", "2026-10-06T13:30:00Z")]
    [InlineData("2026-10-05T23:59:00Z", "2026-10-06T11:45:00Z", "2026-10-06T13:30:00Z")]
    public void Next_IsTheWindowInProgressOrTheNextOne(string now, string opens, string closes)
    {
        var (o, c) = ListeningWindow.Next(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), Noon);

        Assert.Equal(DateTimeOffset.Parse(opens, System.Globalization.CultureInfo.InvariantCulture), o);
        Assert.Equal(DateTimeOffset.Parse(closes, System.Globalization.CultureInfo.InvariantCulture), c);
    }

    [Fact]
    public void Next_SlotJustAfterMidnight_OpensTheEveningBefore()
    {
        var (opens, closes) = ListeningWindow.Next(new DateTimeOffset(2026, 10, 5, 23, 50, 0, TimeSpan.Zero), new TimeOnly(0, 5));

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 23, 50, 0, TimeSpan.Zero), opens);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 1, 35, 0, TimeSpan.Zero), closes);
    }
}
