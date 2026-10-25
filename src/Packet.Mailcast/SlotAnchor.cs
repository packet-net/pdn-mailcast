using System.Globalization;

namespace Packet.Mailcast;

/// <summary>
/// The slot anchor settings: <c>minute</c> (0 to 59, slots counted from 00:MM UTC) and the
/// older <c>timeUtc</c> / <c>slotUtc</c> (HH:mm), which are still read. Only one may be given.
/// </summary>
public static class SlotAnchor
{
    /// <summary>The anchor as HH:mm: <c>00:MM</c> for a valid <paramref name="minute"/>, else the old time, else <paramref name="fallback"/>.</summary>
    public static string Text(string? oldTime, int? minute, string fallback) =>
        minute is >= 0 and <= 59 ? string.Create(CultureInfo.InvariantCulture, $"00:{minute.Value:00}") : oldTime ?? fallback;

    /// <summary>What is wrong with the two settings together, or null: both given, or a minute outside 0 to 59.</summary>
    public static string? Problem(string? oldTime, int? minute, string oldKey, string minuteKey)
    {
        if (oldTime is not null && minute is not null)
        {
            return $"{minuteKey} and {oldKey} are both given: use only {minuteKey} (a minute past the hour, 0 to 59; {oldKey} is the older way to say it)";
        }
        return minute is < 0 or > 59
            ? $"{minuteKey} is {minute}: it must be 0 to 59, the minute past each hour slots start at"
            : null;
    }
}
