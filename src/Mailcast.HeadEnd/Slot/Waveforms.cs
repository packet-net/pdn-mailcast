using System.Globalization;
using M0LTE.Ms110d;
using Packet.Mailcast;
using Packet.SoundModem.Modems;

namespace Mailcast.HeadEnd.Slot;

/// <summary>The waveform one slot goes out on.</summary>
/// <param name="Mode">Its name as configured: pdn-soundmodem's, <c>ms110d-wn4</c> say, or <c>ms110d-wn4-long</c> for the long interleaver.</param>
/// <param name="Airtime">How long a burst takes on it.</param>
/// <param name="SetHardware">
/// The KISS SETHW payload that switches the broadcast modem to it before the slot (waveform
/// number, then 0 for the short interleaver or 1 for the long), or null to leave the modem as it is.
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
/// (command 6: the waveform number and the interleaver, 0 for short and 1 for long) from its next
/// burst on, and echoes the frame back once it has. Its receivers, and every mailcast receiver,
/// decode any MS110D waveform and interleaver by itself (autobaud, from the preamble), so listeners
/// need do nothing.</para>
/// <para>Evening slots (<see cref="SlotTimetable.Evening"/>) may have modes of their own, taken in turn the same way.</para>
/// </remarks>
public sealed class Waveforms
{
    private readonly SlotTimetable _timetable;
    private readonly Dictionary<string, SlotWaveform> _byMode = new(StringComparer.Ordinal);

    /// <param name="modes">The modes in turn; one for every slot.</param>
    /// <param name="setOnModem">Whether to switch the modem to each slot's mode with SETHW; false leaves it as configured.</param>
    /// <param name="timetable">The head end's slots, which the turns are counted in.</param>
    /// <param name="measure">The airtime model for a mode.</param>
    /// <param name="eveningModes">The modes the evening slots take in turn; null for the same as the others.</param>
    public Waveforms(IReadOnlyList<string> modes, bool setOnModem, SlotTimetable timetable, Func<string, IAirtime> measure, IReadOnlyList<string>? eveningModes = null)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(timetable);
        ArgumentNullException.ThrowIfNull(measure);
        if (modes.Count == 0)
        {
            throw new ArgumentException("There must be at least one mode.", nameof(modes));
        }
        if (eveningModes is { Count: 0 })
        {
            throw new ArgumentException("There must be at least one evening mode, or none given.", nameof(eveningModes));
        }
        Modes = [.. modes];
        EveningModes = eveningModes is null ? null : [.. eveningModes];
        SetOnModem = setOnModem;
        _timetable = timetable;
        foreach (string mode in Modes.Concat(EveningModes ?? []).Distinct(StringComparer.Ordinal))
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

    /// <summary>The evening slots' modes in turn, or null when they take the same as the others.</summary>
    public IReadOnlyList<string>? EveningModes { get; }

    /// <summary>Whether each slot switches the modem to its mode.</summary>
    public bool SetOnModem { get; }

    /// <summary>The mode for the slot starting at <paramref name="slot"/>.</summary>
    public string ModeFor(DateTimeOffset slot)
    {
        var modes = EveningModes is { } evening && _timetable.IsEvening(slot) ? evening : Modes;
        if (modes.Count == 1)
        {
            return modes[0];
        }
        var day = DateOnly.FromDateTime(slot.UtcDateTime);
        long sinceAnchor = (slot - BroadcastScheduler.Midnight(day)).Ticks - _timetable.Anchor.Ticks;
        long every = TimeSpan.FromMinutes(_timetable.EveryMinutes).Ticks;
        long perDay = _timetable.SlotsPerDay;
        long slotOfDay = ((Math.DivRem(sinceAnchor, every, out long rem) - (rem < 0 ? 1 : 0)) % perDay + perDay) % perDay;
        return modes[(int)((slotOfDay + day.DayNumber) % modes.Count)];
    }

    /// <summary>The waveform for the slot starting at <paramref name="slot"/>.</summary>
    public SlotWaveform For(DateTimeOffset slot) => _byMode[ModeFor(slot)];

    /// <summary>The ending that asks for the long interleaver: <c>ms110d-wn4-long</c>.</summary>
    public const string LongSuffix = "-long";

    /// <summary>
    /// The SETHW payload for an MS110D mode: <c>ms110d-wnN</c> with N a waveform pdn-soundmodem
    /// sends (0 to 8, or 13), then 0 for the short interleaver; or <c>ms110d-wnN-long</c>, then 1
    /// for the long one, where the waveform has it. Null for anything else.
    /// </summary>
    public static byte[]? SetHardwarePayload(string mode) =>
        Parse(mode) is var (wn, interleaver) ? [(byte)wn, interleaver == Ms110dInterleaverKind.Long ? (byte)1 : (byte)0] : null;

    /// <summary>
    /// The waveform number and interleaver of an MS110D mode as <see cref="SetHardwarePayload"/>
    /// reads it, or null for anything else, including a waveform the modem has no long interleaver
    /// for: the modem would refuse it, so it is refused here first.
    /// </summary>
    public static (int Waveform, Ms110dInterleaverKind Interleaver)? Parse(string mode)
    {
        const string prefix = "ms110d-wn";
        if (mode is null || !mode.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        bool isLong = mode.EndsWith(LongSuffix, StringComparison.Ordinal);
        ReadOnlySpan<char> number = mode.AsSpan(prefix.Length, mode.Length - prefix.Length - (isLong ? LongSuffix.Length : 0));
        if (number.Length is 0 or > 2
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int wn)
            || wn is not (>= 0 and <= 8 or 13))
        {
            return null;
        }
        if (!isLong)
        {
            return (wn, Ms110dInterleaverKind.Short);
        }
        // What the modem does with SETHW: build the transmitter, which throws for a combination
        // MIL-STD-188-110D Phase A does not define.
        try
        {
            _ = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn, Interleaver = Ms110dInterleaverKind.Long });
            return (wn, Ms110dInterleaverKind.Long);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// pdn-soundmodem's modem for a mode, set to it as a slot's SETHW would set it: the catalogue's
    /// <c>ms110d-wnN</c> switched to the long interleaver for <c>ms110d-wnN-long</c>.
    /// </summary>
    public static IModem CreateModem(string mode, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (Parse(mode) is not (_, Ms110dInterleaverKind.Long))
        {
            return ModemCatalog.Create(mode, sampleRate, _ => { });
        }
        IModem modem = ModemCatalog.Create(mode[..^LongSuffix.Length], sampleRate, _ => { });
        if (modem is not IHardwareControllable hardware || !hardware.TrySetHardware(SetHardwarePayload(mode), out _))
        {
            throw new ArgumentException($"pdn-soundmodem's modem would not switch to {mode}", nameof(mode));
        }
        return modem;
    }

    /// <summary>
    /// The airtime model for a mode. The short interleaver keeps the linear model measured from the
    /// modem (<see cref="LinearAirtime.Measure"/>); the long one pads every burst to whole
    /// interleaver blocks, which a linear model cannot see, so it is sized burst by burst as the
    /// modem's own transmitter sizes it (<see cref="Ms110dAirtime"/>).
    /// </summary>
    public static IAirtime Measure(string mode) =>
        Parse(mode) is (int wn, Ms110dInterleaverKind.Long) ? new Ms110dAirtime(wn, Ms110dInterleaverKind.Long) : LinearAirtime.Measure(mode);
}
