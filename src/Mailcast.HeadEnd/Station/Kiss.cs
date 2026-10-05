namespace Mailcast.HeadEnd.Station;

/// <summary>KISS framing: FEND-delimited frames with FESC transparency, as pdn-soundmodem speaks it.</summary>
public static class Kiss
{
    /// <summary>Frame end.</summary>
    public const byte Fend = 0xC0;

    /// <summary>Frame escape.</summary>
    public const byte Fesc = 0xDB;

    /// <summary>Escaped FEND.</summary>
    public const byte Tfend = 0xDC;

    /// <summary>Escaped FESC.</summary>
    public const byte Tfesc = 0xDD;

    /// <summary>A data frame.</summary>
    public const byte DataCommand = 0x00;

    /// <summary>
    /// An ACKMODE data frame: two id octets then the data. pdn-soundmodem sends the two id octets
    /// back, alone, under the same command once the frame's audio has been handed to the sound card.
    /// </summary>
    public const byte AckModeCommand = 0x0C;

    /// <summary>One whole KISS frame on the wire, FENDs included.</summary>
    public static byte[] Encode(int portNibble, byte command, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(portNibble);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(portNibble, 15);
        var bytes = new List<byte>(payload.Length + 8) { Fend, (byte)((portNibble << 4) | (command & 0x0F)) };
        foreach (byte b in payload)
        {
            switch (b)
            {
                case Fend:
                    bytes.Add(Fesc);
                    bytes.Add(Tfend);
                    break;
                case Fesc:
                    bytes.Add(Fesc);
                    bytes.Add(Tfesc);
                    break;
                default:
                    bytes.Add(b);
                    break;
            }
        }
        bytes.Add(Fend);
        return [.. bytes];
    }

    /// <summary>An ACKMODE frame: id low octet, id high octet, then the AX.25 frame.</summary>
    public static byte[] EncodeAckMode(int portNibble, ushort id, ReadOnlySpan<byte> ax25)
    {
        var payload = new byte[ax25.Length + 2];
        payload[0] = (byte)id;
        payload[1] = (byte)(id >> 8);
        ax25.CopyTo(payload.AsSpan(2));
        return Encode(portNibble, AckModeCommand, payload);
    }
}

/// <summary>One frame read off a KISS stream: its type octet and unescaped payload.</summary>
public readonly record struct KissFrame(byte Type, byte[] Payload)
{
    /// <summary>The port nibble.</summary>
    public int Port => Type >> 4;

    /// <summary>The command nibble.</summary>
    public int Command => Type & 0x0F;
}

/// <summary>Reassembles KISS frames from a byte stream that arrives in arbitrary pieces.</summary>
public sealed class KissDecoder
{
    private readonly List<byte> _frame = [];
    private bool _inFrame;
    private bool _escape;

    /// <summary>Feeds received octets and returns any frames they complete.</summary>
    public IReadOnlyList<KissFrame> Feed(ReadOnlySpan<byte> data)
    {
        var frames = new List<KissFrame>();
        foreach (byte b in data)
        {
            if (b == Kiss.Fend)
            {
                if (_inFrame && _frame.Count > 0)
                {
                    frames.Add(new KissFrame(_frame[0], [.. _frame.Skip(1)]));
                }
                _frame.Clear();
                _inFrame = true;
                _escape = false;
                continue;
            }
            if (!_inFrame)
            {
                continue;
            }
            if (_escape)
            {
                _frame.Add(b switch
                {
                    Kiss.Tfend => Kiss.Fend,
                    Kiss.Tfesc => Kiss.Fesc,
                    _ => b,
                });
                _escape = false;
            }
            else if (b == Kiss.Fesc)
            {
                _escape = true;
            }
            else
            {
                _frame.Add(b);
            }
        }
        return frames;
    }
}

/// <summary>An AX.25 UI frame with no digipeaters, without flags or FCS, as KISS carries it.</summary>
public static class Ax25Ui
{
    /// <summary>Control field of a UI frame, poll bit clear.</summary>
    public const byte UiControl = 0x03;

    /// <summary>PID: no layer 3.</summary>
    public const byte NoLayer3 = 0xF0;

    /// <summary>A UI command frame from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public static byte[] Encode(string destination, string source, ReadOnlySpan<byte> information)
    {
        var frame = new byte[16 + information.Length];
        WriteAddress(frame.AsSpan(0, 7), destination, command: true, last: false);
        WriteAddress(frame.AsSpan(7, 7), source, command: false, last: true);
        frame[14] = UiControl;
        frame[15] = NoLayer3;
        information.CopyTo(frame.AsSpan(16));
        return frame;
    }

    /// <summary>Whether a callsign with an optional -SSID can go in an AX.25 address.</summary>
    public static bool IsValidAddress(string callsign)
    {
        try
        {
            _ = Split(callsign);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void WriteAddress(Span<byte> into, string callsign, bool command, bool last)
    {
        var (call, ssid) = Split(callsign);
        for (int i = 0; i < 6; i++)
        {
            char c = i < call.Length ? call[i] : ' ';
            into[i] = (byte)(c << 1);
        }
        // Command frames set C in the destination and clear it in the source (AX.25 2.2, 6.1.2).
        into[6] = (byte)((command ? 0xE0 : 0x60) | (ssid << 1) | (last ? 1 : 0));
    }

    private static (string Call, int Ssid) Split(string callsign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callsign);
        string text = callsign.Trim().ToUpperInvariant();
        int ssid = 0;
        int dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            if (!int.TryParse(text.AsSpan(dash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ssid) || ssid > 15)
            {
                throw new ArgumentException($"'{callsign}' has an SSID that is not 0 to 15.", nameof(callsign));
            }
            text = text[..dash];
        }
        if (text.Length is < 1 or > 6 || text.Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            throw new ArgumentException($"'{callsign}' is not one to six letters and digits.", nameof(callsign));
        }
        return (text, ssid);
    }
}
