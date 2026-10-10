namespace Mailcast.Receiver.Web;

/// <summary>
/// The Channel tile's part of <c>/api/status</c>: the newest slot's picture of the radio path from
/// GB7RDG, and a day of them for the strip.
/// </summary>
internal static class ChannelTile
{
    /// <summary>What the tile says before anything has been measured.</summary>
    public const string Nothing = "Measured after each slot, from the probe after the tone and the bursts this receiver decodes. Nothing measured yet.";

    /// <summary>
    /// The tile's object. <paramref name="lastSlot"/> is the slot the receiver last heard: if it
    /// is over, newer than anything measured, and nothing of it is waiting to be measured, too
    /// little of it decoded (a tone and no data, say), and the tile says so for that slot.
    /// </summary>
    public static object View(ChannelWatch watch, SlotSummary? lastSlot, DateTimeOffset now)
    {
        var history = watch.History;
        var latest = history.Count > 0 ? history[^1] : null;
        if (lastSlot is { } heard && !watch.Measuring && watch.Waiting == 0
            && (heard.Scheduled ?? heard.Started) is var slot && slot > (latest?.Slot ?? DateTimeOffset.MinValue)
            && now - (heard.LastFrame ?? heard.Started) > ChannelWatch.Quiet)
        {
            latest = new ChannelReport { Slot = slot, Measured = now, Words = ChannelReport.TooLittle };
        }
        return new
        {
            slot = latest?.Slot,
            measured = latest?.Measured,
            basis = latest?.Basis ?? "bursts",
            enough = latest?.Enough ?? false,
            modes = (latest?.Modes ?? []).Select(m => new
            {
                label = m.Label,
                delayMs = m.DelayMs,
                powerDb = m.PowerDb,
                dopplerShiftHz = m.DopplerShiftHz,
                dopplerSpreadHz = m.DopplerSpreadHz,
                seenIn = m.SeenIn,
            }),
            delaySpreadMs = latest?.DelaySpreadMs,
            dopplerSpreadHz = latest?.DopplerSpreadHz,
            fadeDb = latest?.FadeDb,
            coherenceS = latest?.CoherenceS,
            coherenceAtLeast = latest?.CoherenceS >= ChannelAnalysis.Longest,
            virtualHeightKm = latest?.VirtualHeightKm,
            snrDb = latest?.SnrDb,
            offsetHz = latest?.OffsetHz,
            distanceKm = latest?.DistanceKm,
            locator = latest?.Locator,
            measurements = latest?.Measurements ?? 0,
            kept = latest?.Kept ?? 0,
            words = latest?.Words ?? Nothing,
            floorDb = latest?.FloorDb,
            profile = latest is { Enough: true } r ? new { startMs = r.ProfileStartMs, stepMs = r.ProfileStepMs, db = r.ProfileDb } : null,
            // The side view of the path (packet-net/pdn-mailcast#50): null when there is nothing
            // plausible to draw (the receiver's place is not known, or nothing could be labelled).
            pathSvg = latest is null ? null : ChannelPicture.Svg(latest, latest.Locator, MainPictureWidthPx, withLabels: true, latest.Place),
            // The same slot measured the other way (the bursts when this is the probe's, or the
            // probe when this is the bursts'), when it was measured both ways.
            other = latest?.Other is { } o ? new
            {
                basis = o.Basis,
                enough = o.Enough,
                modes = o.Modes.Select(m => new
                {
                    label = m.Label,
                    delayMs = m.DelayMs,
                    powerDb = m.PowerDb,
                    dopplerShiftHz = m.DopplerShiftHz,
                    dopplerSpreadHz = m.DopplerSpreadHz,
                    seenIn = m.SeenIn,
                }),
                delaySpreadMs = o.DelaySpreadMs,
                dopplerSpreadHz = o.DopplerSpreadHz,
                virtualHeightKm = o.VirtualHeightKm,
                snrDb = o.SnrDb,
                offsetHz = o.OffsetHz,
                floorDb = o.FloorDb,
                measurements = o.Measurements,
                kept = o.Kept,
                words = o.Words,
            } : null,
            // Bursts heard in the slot on now, measured once it is over.
            waiting = watch.Waiting,
            measuring = watch.Measuring,
            // Bursts left out since the receiver started, too long for the audio it keeps.
            tooLong = watch.TooLong,
            history = history.Select(h => new
            {
                slot = h.Slot,
                basis = h.Basis,
                enough = h.Enough,
                modes = h.Modes.Select(m => new { label = m.Label, delayMs = m.DelayMs, powerDb = m.PowerDb }),
                delaySpreadMs = h.DelaySpreadMs,
                dopplerSpreadHz = h.DopplerSpreadHz,
                virtualHeightKm = h.VirtualHeightKm,
                words = h.Words,
                // A small, unlabelled side view for the day strip; null for the same reasons as
                // the main one above.
                pathSvg = ChannelPicture.Svg(h, h.Locator, StripPictureWidthPx, withLabels: false),
            }),
        };
    }

    /// <summary>The main side view's width, px (packet-net/pdn-mailcast#50): the SVG's own viewBox scales it to fit, phone or desktop.</summary>
    private const int MainPictureWidthPx = 340;

    /// <summary>One day strip picture's width, px.</summary>
    private const int StripPictureWidthPx = 140;
}
