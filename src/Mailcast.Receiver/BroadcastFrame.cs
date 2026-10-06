using System.Text;

namespace Mailcast.Receiver;

/// <summary>
/// Picks the broadcast out of everything the modem decodes: AX.25 UI frames from one of the
/// config's <see cref="ReceiverConfig.AcceptedSources"/> to <see cref="OnAir.Destination"/>, any
/// SSID on either, with no layer 3 protocol. Anything else on the channel is somebody else's traffic.
/// </summary>
internal static class BroadcastFrame
{
    private const byte UiControl = 0x03;
    private const byte NoLayer3 = 0xF0;

    /// <summary>
    /// The information field of a broadcast frame from one of <paramref name="sources"/>, or false
    /// for any other frame. <paramref name="otherSource"/> is the source's base callsign when the
    /// frame looks like the broadcast but comes from a callsign not in the list, and null otherwise.
    /// </summary>
    public static bool TryGetPayload(byte[] frame, CallsignList sources, out ReadOnlyMemory<byte> payload, out string? otherSource)
    {
        payload = default;
        otherSource = null;
        if (!TryReadAddresses(frame, out string destination, out string source, out int afterAddresses))
        {
            return false;
        }
        if (afterAddresses + 2 > frame.Length || frame[afterAddresses] != UiControl || frame[afterAddresses + 1] != NoLayer3)
        {
            return false;
        }
        if (BaseCall(destination) != OnAir.Destination)
        {
            return false;
        }
        if (!sources.Contains(BaseCall(source)))
        {
            otherSource = BaseCall(source);
            return false;
        }
        payload = frame.AsMemory(afterAddresses + 2);
        return true;
    }

    /// <summary>
    /// Reads the destination and source, and skips any digipeaters, returning the offset of the
    /// control octet.
    /// </summary>
    internal static bool TryReadAddresses(ReadOnlySpan<byte> frame, out string destination, out string source, out int afterAddresses)
    {
        destination = source = "";
        afterAddresses = 0;
        int offset = 0;
        for (int field = 0; ; field++)
        {
            if (offset + 7 > frame.Length || field > 9)
            {
                return false;
            }
            string? call = ReadCall(frame.Slice(offset, 7));
            if (call is null)
            {
                return false;
            }
            if (field == 0)
            {
                destination = call;
            }
            else if (field == 1)
            {
                source = call;
            }
            bool last = (frame[offset + 6] & 1) != 0;
            offset += 7;
            if (last)
            {
                if (field == 0)
                {
                    return false;
                }
                afterAddresses = offset;
                return true;
            }
        }
    }

    private static string? ReadCall(ReadOnlySpan<byte> field)
    {
        var call = new StringBuilder(9);
        for (int i = 0; i < 6; i++)
        {
            char c = (char)(field[i] >> 1);
            if (c == ' ')
            {
                continue;
            }
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            {
                return null;
            }
            call.Append(c);
        }
        if (call.Length == 0)
        {
            return null;
        }
        int ssid = (field[6] >> 1) & 0x0F;
        return ssid == 0 ? call.ToString() : $"{call}-{ssid}";
    }

    private static string BaseCall(string call)
    {
        int dash = call.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? call : call[..dash];
    }
}
