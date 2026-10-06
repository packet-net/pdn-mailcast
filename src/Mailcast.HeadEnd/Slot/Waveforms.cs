using System.Globalization;
using Packet.Mailcast;

namespace Mailcast.HeadEnd.Slot;

/// <summary>The waveform one slot goes out on.</summary>
/// <param name="Mode">pdn-soundmodem's name for it, <c>ms110d-wn4</c> say.</param>
/// <param name="Airtime">How long a burst takes on it.</param>
/// <param name="SetHardware">
/// The KISS SETHW payload that switches the broadcast modem to it before the slot (waveform
/// number, then 0 for the short interleaver), or null to leave the modem as it is.
/// </param>
public sealed record SlotWaveform(string Mode, IAirtime Airtime, byte[]? SetHardware);

/// <summary>
/// Which waveform each slot uses: one for every slot, or several taking turns slot by slot.
/// </summary>
/// <remarks>
/// <para>With several, slot n of the day (counted from the timetable's anchor) on day d uses mode
/// (n + d) mod the number of modes. Consecutive slots take turns, and the next day starts one
/// further on, so with two modes each hour gets each mode on
/// alternate days. A slot run again (a retry or a restart) gets the same mode, and nothing needs
/// remembering.</para>
/// <para>pdn-soundmodem's MS110D modem changes its transmit waveform on a KISS SETHW frame
/// (command 6: the waveform number and the interleaver, 0 for short) from its next burst on, and
/// echoes the frame back once it has. Its receivers, and every mailcast receiver, decode any MS110D
/// waveform by itself (autobaud), so listeners need do nothing.</para>
/// </remarks>
public sealed class Waveforms
{
    private readonly SlotTimetable _timetable;
    private readonly Dictionary<string, SlotWaveform> _byMode = new(StringComparer.Ordinal);

    /// <param name="modes">The modes in turn; one for every slot.</param>
    /// <param name="setOnModem">Whether to switch the modem to each slot's mode with SETHW; false leaves it as configured.</param>
    /// <param name="timetable">The head end's slots, which the turns are counted in.</param>
    /// <param name="measure">The airtime model for a mode.</param>
    public Waveforms(IReadOnlyList<string> modes, bool setOnModem, SlotTimetable timetable, Func<string, IAirtime> measure)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(timetable);
        ArgumentNullException.ThrowIfNull(measure);
        if (modes.Count == 0)
        {
            throw new ArgumentException("There must be at least one mode.", nameof(modes));
        }
        Modes = [.. modes];
        SetOnModem = setOnModem;
        _timetable = timetable;
        foreach (string mode in Modes.Distinct(StringComparer.Ordinal))
        {
            byte[]? payload = null;
            if (setOnModem)
            {
                payload = SetHardwarePayload(mode) ?? throw new ArgumentException($"'{mode}' is not an MS110D waveform the modem can switch to.", nameof(modes));
            }
            _byMode[mode] = new SlotWaveform(mode, measure(mode), payload);
        }
    }

    /// <summary>The modes in turn.</summary>
    public IReadOnlyList<string> Modes { get; }

    /// <summary>Whether each slot switches the modem to its mode.</summary>
    public bool SetOnModem { get; }

    /// <summary>The mode for the slot starting at <paramref name="slot"/>.</summary>
    public string ModeFor(DateTimeOffset slot)
    {
        if (Modes.Count == 1)
        {
            return Modes[0];
        }
        var day = DateOnly.FromDateTime(slot.UtcDateTime);
        long sinceAnchor = (slot - BroadcastScheduler.Midnight(day)).Ticks - _timetable.Anchor.Ticks;
        long every = TimeSpan.FromMinutes(_timetable.EveryMinutes).Ticks;
        long perDay = _timetable.SlotsPerDay;
        long slotOfDay = ((Math.DivRem(sinceAnchor, every, out long rem) - (rem < 0 ? 1 : 0)) % perDay + perDay) % perDay;
        return Modes[(int)((slotOfDay + day.DayNumber) % Modes.Count)];
    }

    /// <summary>The waveform for the slot starting at <paramref name="slot"/>.</summary>
    public SlotWaveform For(DateTimeOffset slot) => _byMode[ModeFor(slot)];

    /// <summary>
    /// The SETHW payload for an MS110D mode, <c>ms110d-wnN</c> with N a waveform pdn-soundmodem
    /// sends (0 to 8, or 13): N, then 0 for the short interleaver. Null for anything else.
    /// </summary>
    public static byte[]? SetHardwarePayload(string mode)
    {
        const string prefix = "ms110d-wn";
        if (mode is null || !mode.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(mode.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int wn)
            || mode.Length - prefix.Length > 2 || wn is not (>= 0 and <= 8 or 13))
        {
            return null;
        }
        return [(byte)wn, 0];
    }
}
