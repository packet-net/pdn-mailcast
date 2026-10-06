using System.Globalization;
using System.Reflection;
using Packet.SoundModem.Ms110d;

namespace Mailcast.Receiver;

/// <summary>MS110D waveforms by the names pdn-soundmodem gives them (<c>ms110d-wn4</c>), and in words.</summary>
public static class Waveform
{
    private const string Prefix = "ms110d-wn";

    /// <summary>The name of waveform number <paramref name="wn"/>.</summary>
    public static string Name(int wn) => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{wn}");

    /// <summary>The waveform number in a name, or null for a name that is not one.</summary>
    public static int? Number(string? name) =>
        name is not null && name.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int wn)
            ? wn : null;

    /// <summary>The user data rate in bits/s, from pdn-soundmodem's 3 kHz table; null for one it does not have.</summary>
    public static int? Bps(string? name)
    {
        if (Number(name) is not { } wn)
        {
            return null;
        }
        try
        {
            return Ms110dMode.Mode3k(wn).Bps;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>For the page and the log: <c>1200 bps (WN4)</c>.</summary>
    public static string Words(string name) =>
        Bps(name) is { } bps && Number(name) is { } wn
            ? string.Create(CultureInfo.InvariantCulture, $"{bps} bps (WN{wn})")
            : Ascii.Clean(name);
}

/// <summary>One MS110D burst: the waveform the modem's autobaud locked to, and the frames read from it.</summary>
/// <param name="Waveform">The waveform, as <see cref="Mailcast.Receiver.Waveform.Name"/> names it.</param>
/// <param name="Started">When the modem locked to it.</param>
/// <param name="Frames">Frames read from it so far.</param>
/// <param name="Ended">When it ended; null while the modem is still on it.</param>
public sealed record HeardBurst(string Waveform, DateTimeOffset Started, int Frames, DateTimeOffset? Ended)
{
    /// <summary>The modem is on this burst now.</summary>
    public bool Live => Ended is null;
}

/// <summary>
/// Follows the MS110D receiver's autobaud from burst to burst, for the page's speed tile: the
/// burst it is on now, or else the last one any frames were read from.
/// </summary>
/// <remarks>
/// Driven on the audio thread (<see cref="AfterBlock"/> and <see cref="OnFrame"/>); <see cref="Shown"/>
/// is a snapshot swapped whole, safe to read from any thread. A burst nothing was read from (a
/// preamble heard through noise) is shown while it lasts but not kept after: the tile is about
/// the frames being heard.
/// </remarks>
public sealed class BurstWatch(TimeProvider time)
{
    private HeardBurst? _current;
    private HeardBurst? _last;
    private volatile HeardBurst? _shown;

    /// <summary>The burst the modem is on now, or else the last one frames were read from; null before any.</summary>
    public HeardBurst? Shown => _shown;

    /// <summary>After each block of audio: the waveform number the modem is locked to, or null.</summary>
    public void AfterBlock(int? waveformNumber)
    {
        string? name = waveformNumber is >= 0 and int wn ? Waveform.Name(wn) : null;
        if (_current is { } current && current.Waveform != name)
        {
            End();
        }
        if (name is not null && _current is null)
        {
            Start(name);
        }
    }

    /// <summary>A frame was read, while the modem was locked to <paramref name="waveformNumber"/> (null if it cannot say).</summary>
    public void OnFrame(int? waveformNumber)
    {
        if (waveformNumber is not (>= 0 and int wn))
        {
            return;
        }
        string name = Waveform.Name(wn);
        if (_current?.Waveform != name)
        {
            End();
            Start(name);
        }
        _current = _current! with { Frames = _current.Frames + 1 };
        _shown = _current;
    }

    private void Start(string name)
    {
        _current = new HeardBurst(name, time.GetUtcNow(), 0, null);
        _shown = _current;
    }

    private void End()
    {
        if (_current is { Frames: > 0 } done)
        {
            _last = done with { Ended = time.GetUtcNow() };
        }
        _current = null;
        _shown = _last;
    }

    /// <summary>
    /// The MS110D receiver inside <paramref name="modem"/>, whose <see cref="Ms110dDemodulator.Lock"/>
    /// is the autobaud result; null if this pdn-soundmodem keeps it elsewhere.
    /// </summary>
    /// <remarks>
    /// pdn-soundmodem (0.86.0) makes the receiver's lock public on <see cref="Ms110dDemodulator"/>,
    /// but <see cref="Ms110dModem"/> keeps its receiver in a private field and does not pass the
    /// lock on (its <c>Mode</c> and each frame's <c>FrameQuality.Mode</c> are the transmit
    /// waveform). So the field is read here, once, and a test pins it: a pdn-soundmodem that
    /// renames it fails that test rather than quietly losing the speed.
    /// </remarks>
    internal static Ms110dDemodulator? ReceiverOf(Ms110dModem modem) =>
        typeof(Ms110dModem).GetField("_rx", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(modem) as Ms110dDemodulator;
}
