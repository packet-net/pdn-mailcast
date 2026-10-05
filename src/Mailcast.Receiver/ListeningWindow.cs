using System.Globalization;

namespace Mailcast.Receiver;

/// <summary>When a web SDR is listened to: from a little before the daily slot to well after its start.</summary>
public static class ListeningWindow
{
    /// <summary>
    /// The window in progress at <paramref name="now"/>, or else the next one. Its opening is at
    /// or before <paramref name="now"/> exactly when the web SDR should be open now.
    /// </summary>
    public static (DateTimeOffset Opens, DateTimeOffset Closes) Next(DateTimeOffset now, TimeOnly slot)
    {
        var utc = now.ToUniversalTime();
        var today = new DateTimeOffset(DateOnly.FromDateTime(utc.UtcDateTime).ToDateTime(slot), TimeSpan.Zero);
        foreach (var start in new[] { today.AddDays(-1), today, today.AddDays(1) })
        {
            var opens = start - ReceiverConfig.WebSdrBefore;
            var closes = start + ReceiverConfig.WebSdrAfter;
            if (utc < closes)
            {
                return (opens, closes);
            }
        }
        throw new InvalidOperationException("unreachable: tomorrow's window always closes after now");
    }

    /// <summary>The window in words, for the page and the log.</summary>
    public static string Describe(TimeOnly slot) => string.Create(CultureInfo.InvariantCulture,
        $"open {slot.Add(-ReceiverConfig.WebSdrBefore):HH:mm} to {slot.Add(ReceiverConfig.WebSdrAfter):HH:mm} UTC daily to save its listening allowance");
}
