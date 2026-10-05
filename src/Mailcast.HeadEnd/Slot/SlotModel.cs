using System.Text.Json.Serialization;

namespace Mailcast.HeadEnd.Slot;

/// <summary>What to do when the channel is still busy at the end of the wait.</summary>
public enum BusyPolicy
{
    /// <summary>Carry on without the calibration tone.</summary>
    Go,

    /// <summary>Give up on this slot; everything rolls on to the next.</summary>
    Skip,
}

/// <summary>What to do when the Flex cannot be reached.</summary>
public enum FlexUnreachablePolicy
{
    /// <summary>Log it and broadcast without the PA temperature watch.</summary>
    CarryOn,

    /// <summary>Do not broadcast without it.</summary>
    Skip,
}

/// <summary>How the slot runs. Everything has the design's default.</summary>
public sealed record SlotSettings
{
    /// <summary>The AX.25 source of every frame: the station's callsign.</summary>
    public string Callsign { get; init; } = "GB7RDG";

    /// <summary>The AX.25 destination of every frame.</summary>
    public string Destination { get; init; } = "MCAST";

    /// <summary>The broadcast modem's sub-channel on the station, which the lease and the tone name.</summary>
    public int SubChannel { get; init; }

    /// <summary>How long each lease request asks for.</summary>
    public TimeSpan LeaseLength { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How often the lease is renewed.</summary>
    public TimeSpan RenewEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Lease time that must remain, beyond the airtime of everything queued, before more frames are
    /// queued: room for carrier sense and the modem's gather time, so a burst never runs past the
    /// lease even if the next renewal fails.
    /// </summary>
    public TimeSpan LeaseMargin { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The lease's <c>maxCarrierWaitSeconds</c>: the station sends the holder's frames anyway once
    /// they have waited this long for a clear channel. At most <see cref="LeaseMargin"/>, so a
    /// burst that waits its longest still ends inside the lease.
    /// </summary>
    public TimeSpan MaxCarrierWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Refuse to key until the system clock is synchronised: a slot timed by a wrong clock goes out
    /// when nobody is listening, and possibly over somebody else.
    /// </summary>
    public bool RequireClockSync { get; init; } = true;

    /// <summary>How long to keep trying for a clear channel.</summary>
    public TimeSpan ChannelWait { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>What to do if it never clears.</summary>
    public BusyPolicy WhenStillBusy { get; init; } = BusyPolicy.Go;

    /// <summary>The calibration tone's length; zero sends none.</summary>
    public TimeSpan ToneLength { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The calibration tone's audio frequency: the signal centre.</summary>
    public double ToneHz { get; init; } = 1800;

    /// <summary>
    /// A pause after the tone so the modem's CW ident, which falls due with the first transmission
    /// and which the station polls for every 5 s, goes out between the tone and the first burst.
    /// </summary>
    public TimeSpan PauseAfterTone { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>The longest burst the modem packs (its <c>maxBurstSeconds</c>).</summary>
    public TimeSpan MaxBurst { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Frames to queue per burst. Null works it out from the airtime model.</summary>
    public int? FramesPerBurst { get; init; }

    /// <summary>A pause between one burst's acknowledgements and queueing the next.</summary>
    public TimeSpan BurstGap { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long past a burst's expected airtime to wait for its acknowledgements.</summary>
    public TimeSpan AckGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The hard stop: the slot never runs longer than this from its start.</summary>
    public TimeSpan MaxSlotLength { get; init; } = TimeSpan.FromMinutes(40);

    /// <summary>Stop when the Flex's PA passes this many degrees C.</summary>
    public double PaTemperatureLimitC { get; init; } = 70;

    /// <summary>What to do when the Flex cannot be reached.</summary>
    public FlexUnreachablePolicy WhenFlexUnreachable { get; init; } = FlexUnreachablePolicy.CarryOn;

    /// <summary>How often the PA temperature is read during the slot.</summary>
    public TimeSpan PaCheckEvery { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>One frame to broadcast: the mailcast payload, and what it is for the log.</summary>
public sealed record SlotFrame(byte[] Payload, ulong ObjectId, uint Esi);

/// <summary>How a slot ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SlotOutcome>))]
public enum SlotOutcome
{
    /// <summary>Every planned frame went out.</summary>
    Completed,

    /// <summary>It started but stopped early; <see cref="SlotReport.Reason"/> says why.</summary>
    Aborted,

    /// <summary>Nothing was transmitted; <see cref="SlotReport.Reason"/> says why.</summary>
    Skipped,
}

/// <summary>What happened in one slot. This is also what the status endpoint shows.</summary>
public sealed record SlotReport
{
    /// <summary>The slot's start. A report from before slots had times leaves it unset: see <see cref="Day"/>.</summary>
    public DateTimeOffset Slot { get; init; }

    /// <summary>The slot's day, UTC.</summary>
    public DateOnly Day { get; init; }

    public DateTimeOffset Start { get; init; }

    public DateTimeOffset End { get; init; }

    public SlotOutcome Outcome { get; init; }

    /// <summary>Why it was skipped or aborted; null when it completed.</summary>
    public string? Reason { get; init; }

    public int FramesPlanned { get; init; }

    /// <summary>Frames the modem acknowledged: on the air.</summary>
    public int FramesSent { get; init; }

    /// <summary>
    /// Frames handed to the modem, acknowledged or not. Their ESIs count as used: one that may have
    /// gone out is never sent again.
    /// </summary>
    public int FramesQueued { get; init; }

    public int Bursts { get; init; }

    public int BulletinsInRotation { get; init; }

    public bool ToneSent { get; init; }

    public string Reference { get; init; } = "not read";

    public double? PaTemperatureMaxC { get; init; }

    public bool LeaseTaken { get; init; }

    /// <summary>True when it was skipped for a reason at the station that may clear, so it is worth trying the same slot again.</summary>
    public bool Retryable { get; init; }

    public int LeaseRenewals { get; init; }

    /// <summary>Who asked for a one-off slot (<c>POST /run</c>), or null for a scheduled one.</summary>
    public string? RequestedBy { get; init; }
}
