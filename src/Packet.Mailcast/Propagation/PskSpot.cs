using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Mailcast.Propagation;

/// <summary>The two bands the PSK Reporter reading watches.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PskBand>))]
public enum PskBand : byte
{
    /// <summary>40 m, the band the mailcast goes out on, and the one sent on the air.</summary>
    [JsonStringEnumMemberName("40m")]
    Forty = 40,

    /// <summary>80 m, for context: shown at the head end, not sent.</summary>
    [JsonStringEnumMemberName("80m")]
    Eighty = 80,
}

/// <summary>
/// One PSK Reporter spot between two stations in the UK or Ireland, as the reading keeps it: when
/// the transmission was, the band, the great-circle distance between the two locators, the SNR
/// the receiver reported, and both callsigns.
/// </summary>
/// <param name="Sequence">The feed's own sequence number (<c>sq</c>), unique per spot.</param>
/// <param name="Time">When the transmission started (<c>t_tx</c>, else <c>t</c>).</param>
/// <param name="Band">40 or 80 m.</param>
/// <param name="DistanceKm">The great-circle distance between the two locators' centres.</param>
/// <param name="SnrDb">The SNR the receiver reported, dB; null when the feed gives none, as it sometimes does.</param>
/// <param name="Sender">The transmitting station's callsign.</param>
/// <param name="Receiver">The receiving station's callsign.</param>
public readonly record struct PskSpot(long Sequence, DateTimeOffset Time, PskBand Band, int DistanceKm, int? SnrDb, string Sender, string Receiver);

/// <summary>
/// Reads PSK Reporter's public MQTT feed (<c>mqtt.pskreporter.info</c>), whose topics are
/// <c>pskr/filter/v2/{band}/{mode}/{sendercall}/{receivercall}/{senderlocator}/{receiverlocator}/{sendercountry}/{receivercountry}</c>
/// and whose payloads are JSON with short names: <c>sq</c>, <c>f</c>, <c>md</c>, <c>rp</c>
/// (the SNR, sometimes null), <c>t</c>, <c>t_tx</c>, <c>sc</c>, <c>sl</c>, <c>rc</c>, <c>rl</c>,
/// <c>sa</c>, <c>ra</c> and <c>b</c>. Countries are DXCC entity numbers; in the topic, a slash in
/// a callsign is a dot and the locators are cut to 4 characters, so the payload is what counts.
/// </summary>
public static class PskFeed
{
    /// <summary>
    /// The DXCC entities of the UK and Ireland: England 223, Wales 294, Scotland 279, Northern
    /// Ireland 265, Ireland 245, the Isle of Man 114, Guernsey 106 and Jersey 122.
    /// </summary>
    public static IReadOnlyList<int> Entities { get; } = [223, 294, 279, 265, 245, 114, 106, 122];

    /// <summary>The modes counted: their SNRs are all in 2500 Hz, so one median means something.</summary>
    public static IReadOnlyList<string> Modes { get; } = ["FT8", "FT4", "WSPR"];

    /// <summary>
    /// The topic filters to subscribe to: 40 and 80 m, every mode, with both ends in the UK or
    /// Ireland. 2 bands by 8 by 8 entities, 128 filters, which the broker takes in one SUBSCRIBE.
    /// </summary>
    public static IReadOnlyList<string> TopicFilters { get; } =
        [.. from band in new[] { "40m", "80m" }
            from sender in Entities
            from receiver in Entities
            select $"pskr/filter/v2/{band}/+/+/+/+/+/{sender}/{receiver}"];

    /// <summary>The longest payload taken: a real spot is about 200 octets.</summary>
    public const int MostPayload = 1024;

    /// <summary>The longest string field taken, before unescaping: a locator is 10, a mode or band a few.</summary>
    public const int MostField = 16;

    /// <summary>
    /// The shortest and longest callsign taken, of A to Z, 0 to 9, the slash and the hyphen (the
    /// live feed has receivers such as G4WNC-1).
    /// </summary>
    public const int ShortestCall = 3, LongestCall = 15;

    /// <summary>
    /// A spot from a payload, or false for anything this reading does not count: another band or
    /// mode, an end outside the UK and Ireland, a callsign or locator that is not one, a field
    /// longer than any real one, or JSON that is not a spot. Nothing in a payload, however long
    /// or strange, is kept beyond these bounds: a broker on plain MQTT is not to be trusted.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out PskSpot spot)
    {
        spot = default;
        if (payload.Length > MostPayload)
        {
            return false;
        }
        try
        {
            var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 4 });
            long? sq = null, t = null, tTx = null, sa = null, ra = null;
            int? rp = null;
            string? md = null, sc = null, sl = null, rc = null, rl = null, b = null;
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                // Matched in place: a property name is never turned into a string.
                int field = reader.ValueTextEquals("sq") ? 1 : reader.ValueTextEquals("t") ? 2 : reader.ValueTextEquals("t_tx") ? 3
                    : reader.ValueTextEquals("sa") ? 4 : reader.ValueTextEquals("ra") ? 5 : reader.ValueTextEquals("rp") ? 6
                    : reader.ValueTextEquals("md") ? 7 : reader.ValueTextEquals("sc") ? 8 : reader.ValueTextEquals("sl") ? 9
                    : reader.ValueTextEquals("rc") ? 10 : reader.ValueTextEquals("rl") ? 11 : reader.ValueTextEquals("b") ? 12 : 0;
                if (!reader.Read())
                {
                    return false;
                }
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                    continue;
                }
                switch (field)
                {
                    case 1: sq = Long(ref reader); break;
                    case 2: t = Long(ref reader); break;
                    case 3: tTx = Long(ref reader); break;
                    case 4: sa = Long(ref reader); break;
                    case 5: ra = Long(ref reader); break;
                    case 6: rp = Long(ref reader) is { } v and >= -99 and <= 99 ? (int)v : null; break;
                    case 7: md = Text(ref reader); break;
                    case 8: sc = Text(ref reader); break;
                    case 9: sl = Text(ref reader); break;
                    case 10: rc = Text(ref reader); break;
                    case 11: rl = Text(ref reader); break;
                    case 12: b = Text(ref reader); break;
                    default: break;
                }
            }
            PskBand band;
            if (b == "40m")
            {
                band = PskBand.Forty;
            }
            else if (b == "80m")
            {
                band = PskBand.Eighty;
            }
            else
            {
                return false;
            }
            if (sq is not (> 0 and { } sequence) || (tTx ?? t) is not (>= EarliestTime and <= LatestTime and { } when) || md is null || !Modes.Contains(md)
                || !IsEntity(sa) || !IsEntity(ra)
                || Callsign(sc) is not { } sender || Callsign(rc) is not { } receiver
                || !TryLocate(sl, out double lat1, out double lon1) || !TryLocate(rl, out double lat2, out double lon2))
            {
                return false;
            }
            spot = new PskSpot(sequence, DateTimeOffset.FromUnixTimeSeconds(when), band,
                (int)Math.Round(DistanceKm(lat1, lon1, lat2, lon2)), rp, sender, receiver);
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // 2000-01-01 to 2100-01-01: anything else is not a time this feed sends.
    private const long EarliestTime = 946_684_800, LatestTime = 4_102_444_800;

    /// <summary>Whether a country field is one of <see cref="Entities"/>, checked before any narrowing, so 2^32 + 223 is not England.</summary>
    private static bool IsEntity(long? value) => value is > 0 and < 1000 && Entities.Contains((int)value.Value);

    /// <summary>A callsign in capitals, or null unless it is <see cref="ShortestCall"/> to <see cref="LongestCall"/> of A to Z, 0 to 9, the slash and the hyphen.</summary>
    public static string? Callsign(string? text)
    {
        if (text is null || text.Length is < ShortestCall or > LongestCall)
        {
            return null;
        }
        foreach (char c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '-'))
            {
                return null;
            }
        }
        return text.ToUpperInvariant();
    }

    /// <summary>
    /// A locator's centre: the first 6 characters of an 8 or 10 character one (a subsquare is
    /// about 5 by 9 km, plenty here), or a 4 or 6 character one as it is.
    /// </summary>
    public static bool TryLocate(string? locator, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (locator is null)
        {
            return false;
        }
        string l = locator;
        // Characters 7 and 8 are digits and 9 and 10 letters A to X: checked, though only the first 6 are used.
        if (l.Length is 8 or 10 && !(char.IsAsciiDigit(l[6]) && char.IsAsciiDigit(l[7])))
        {
            return false;
        }
        if (l.Length == 10 && !(char.ToUpperInvariant(l[8]) is >= 'A' and <= 'X' && char.ToUpperInvariant(l[9]) is >= 'A' and <= 'X'))
        {
            return false;
        }
        return l.Length switch
        {
            4 or 6 => Maidenhead.TryParse(l, out latitude, out longitude),
            8 or 10 => Maidenhead.TryParse(l[..6], out latitude, out longitude),
            _ => false,
        };
    }

    /// <summary>The great-circle distance between two points, km, on a sphere of 6371 km.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthKm = 6371;
        double p1 = double.DegreesToRadians(lat1), p2 = double.DegreesToRadians(lat2);
        double dp = p2 - p1, dl = double.DegreesToRadians(lon2 - lon1);
        double h = (Math.Sin(dp / 2) * Math.Sin(dp / 2)) + (Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2));
        return 2 * EarthKm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static long? Long(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long v) ? v
        : reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double d) && double.IsFinite(d) && Math.Abs(d) < 1e15 ? (long)Math.Round(d)
        : null;

    private static string? Text(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.String && !reader.HasValueSequence && reader.ValueSpan.Length <= MostField ? reader.GetString() : null;
}
