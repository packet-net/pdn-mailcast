using System.Text.Json;
using Mailcast.RaptorQ;
using Packet.Mailcast.Propagation;

namespace Packet.Mailcast.Tests;

/// <summary>
/// The PSK Reporter reading: reading the feed's real payloads, binning by distance, the open and
/// closed thresholds and what a closure must rest on, the skip zone, and content type 4 source 3
/// on the air and at receivers old and new.
/// </summary>
public class PskReporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FullWindow = PskEvaluator.Window;

    private static long _sequence = 1_000;

    /// <summary>A spot <paramref name="minutesAgo"/> before <see cref="Now"/>, between two made-up stations by number.</summary>
    private static PskSpot Spot(int km, int from, int to, PskBand band = PskBand.Forty, int? snr = -10, double minutesAgo = 5) =>
        new(Interlocked.Increment(ref _sequence), Now.AddMinutes(-minutesAgo), band, km, snr, $"G{from}AA", $"M{to}BB");

    /// <summary><paramref name="count"/> spots at <paramref name="km"/> among <paramref name="stations"/> callsigns, paired round robin.</summary>
    private static IEnumerable<PskSpot> Many(int count, int km, int stations, PskBand band = PskBand.Forty, int snr = -10)
    {
        for (int i = 0; i < count; i++)
        {
            // Senders and receivers drawn from one pool of numbers, so "stations" is exact.
            int a = i % stations, b = (i + 1) % stations;
            yield return new(Interlocked.Increment(ref _sequence), Now.AddMinutes(-5), band, km + (i % 7), snr + (i % 3), $"S{a}", $"S{b}");
        }
    }

    private static PskReading Evaluate(IEnumerable<PskSpot> spots, TimeSpan? up = null, bool connected = true) =>
        PskEvaluator.Evaluate(spots, Now, up ?? FullWindow, connected);

    private static IEnumerable<(string Topic, string Payload)> Fixture()
    {
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Propagation", "pskreporter-uk-2026-10-06.jsonl")))
        {
            using var doc = JsonDocument.Parse(line);
            yield return (doc.RootElement.GetProperty("topic").GetString()!, doc.RootElement.GetProperty("payload").GetString()!);
        }
    }

    [Fact]
    public void TheSubscription_Is40And80m_WithBothEndsInTheUkOrIreland_By_DxccNumber()
    {
        Assert.Equal(128, PskFeed.TopicFilters.Count);
        Assert.Contains("pskr/filter/v2/40m/+/+/+/+/+/223/245", PskFeed.TopicFilters);
        Assert.Contains("pskr/filter/v2/80m/+/+/+/+/+/122/114", PskFeed.TopicFilters);
        Assert.All(PskFeed.TopicFilters, f => Assert.Equal(11, f.Split('/').Length));
        Assert.Equal([223, 294, 279, 265, 245, 114, 106, 122], PskFeed.Entities);
    }

    [Fact]
    public void ALiveSample_Decodes_KeepingFt8Ft4AndWspr_BetweenUkAndIrishStations_Only()
    {
        // 34 messages heard on 2026-10-06 at about 19:35 UTC: 32 from the narrow subscription, and
        // two with a Dutch receiver from a wider one. Two of the 32 are CW, which is not counted.
        var all = Fixture().ToList();
        Assert.Equal(34, all.Count);
        var spots = all.Select(m => PskFeed.TryParse(System.Text.Encoding.UTF8.GetBytes(m.Payload), out var s) ? s : (PskSpot?)null).ToList();
        Assert.Equal(30, spots.Count(s => s is not null));
        Assert.Equal(16, spots.Count(s => s?.Band == PskBand.Forty));
        // The feed sometimes gives no SNR, and the spot still counts.
        Assert.Equal(4, spots.Count(s => s is { SnrDb: null }));

        // The topic's countries match the payload's.
        foreach (var (topic, payload) in all)
        {
            using var doc = JsonDocument.Parse(payload);
            string[] levels = topic.Split('/');
            Assert.Equal(doc.RootElement.GetProperty("sa").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), levels[9]);
            Assert.Equal(doc.RootElement.GetProperty("ra").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), levels[10]);
        }

        // An FT4 spot from Leeds to Inverness, 6 character locators.
        var ft4 = spots.Single(s => s?.Sequence == 73463952633)!.Value;
        Assert.Equal((PskBand.Forty, 312, -12, "M0XXB", "M9PSY"), (ft4.Band, ft4.DistanceKm, ft4.SnrDb, ft4.Sender, ft4.Receiver));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791315300), ft4.Time);
        // WSPR from a 4 character locator; an 8 character one is taken to its first 6.
        Assert.Equal(503, spots.Single(s => s?.Sequence == 73463973782)!.Value.DistanceKm);
        Assert.Equal(758, spots.Single(s => s?.Sequence == 73463948049)!.Value.DistanceKm);
        Assert.Null(spots.Single(s => s?.Sequence == 73463948049)!.Value.SnrDb);
    }

    [Theory]
    [InlineData("{\"sq\":1,\"md\":\"FT8\",\"rp\":-5,\"t\":1791315000,\"sc\":\"G1A\",\"sl\":\"IO91\",\"rc\":\"G2B\",\"rl\":\"IO93\",\"sa\":223,\"ra\":223,\"b\":\"20m\"}")]
    [InlineData("{\"sq\":1,\"md\":\"CW\",\"rp\":-5,\"t\":1791315000,\"sc\":\"G1A\",\"sl\":\"IO91\",\"rc\":\"G2B\",\"rl\":\"IO93\",\"sa\":223,\"ra\":223,\"b\":\"40m\"}")]
    [InlineData("{\"sq\":1,\"md\":\"FT8\",\"rp\":-5,\"t\":1791315000,\"sc\":\"G1A\",\"sl\":\"IO91\",\"rc\":\"PA2B\",\"rl\":\"JO22\",\"sa\":223,\"ra\":263,\"b\":\"40m\"}")]
    [InlineData("{\"sq\":1,\"md\":\"FT8\",\"rp\":-5,\"t\":1791315000,\"sc\":\"G1A\",\"sl\":\"\",\"rc\":\"G2B\",\"rl\":\"IO93\",\"sa\":223,\"ra\":223,\"b\":\"40m\"}")]
    [InlineData("{\"md\":\"FT8\",\"rp\":-5,\"t\":1791315000,\"sc\":\"G1A\",\"sl\":\"IO91\",\"rc\":\"G2B\",\"rl\":\"IO93\",\"sa\":223,\"ra\":223,\"b\":\"40m\"}")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void AnythingElse_IsNotASpot(string payload) =>
        Assert.False(PskFeed.TryParse(System.Text.Encoding.UTF8.GetBytes(payload), out _));

    [Theory]
    [InlineData(29, -1)]
    [InlineData(30, 0)]
    [InlineData(249, 0)]
    [InlineData(250, 1)]
    [InlineData(699, 1)]
    [InlineData(700, 2)]
    [InlineData(1200, 2)]
    [InlineData(1201, -1)]
    public void Paths_AreBinnedByDistance_GroundWaveAndBeyond1200KmLeftOut(int km, int bin) =>
        Assert.Equal(bin, PskEvaluator.BinOf(km));

    [Fact]
    public void ADistance_IsOpen_WithFiveSpotsFromFourStations_AndNotWithLess()
    {
        Assert.Equal(PathVerdict.Open, Evaluate(Many(5, 400, 4)).At500);
        Assert.Equal(PathVerdict.NoData, Evaluate(Many(4, 400, 4)).At500);
        // Five spots of one pair of stations is one path heard five times, not an opening.
        Assert.Equal(PathVerdict.NoData, Evaluate(Many(5, 400, 2)).At500);
        Assert.Equal(PathVerdict.NoData, Evaluate(Many(5, 400, 3)).At500);
    }

    [Fact]
    public void Silence_IsNotClosure_WithoutEvidence()
    {
        // Nothing at all: no data anywhere, not closed.
        var nothing = Evaluate([]);
        Assert.Equal((PathVerdict.NoData, PathVerdict.NoData, PathVerdict.NoData), (nothing.At100, nothing.At500, nothing.At1000));
        Assert.Equal(IonoState.Unknown, nothing.State);
        Assert.Equal("Too few spots to say", nothing.Headline());

        // A little activity further out is not enough to call the short paths closed.
        var thin = Evaluate(Many(39, 400, 20));
        Assert.Equal(PathVerdict.Open, thin.At500);
        Assert.Equal(PathVerdict.NoData, thin.At100);
        var few = Evaluate(Many(60, 400, 11));
        Assert.Equal(PathVerdict.NoData, few.At100);
    }

    [Fact]
    public void ADistance_IsClosed_WhenTheBandIsBusyElsewhere_AndAStrayOrTwoIsAllowed()
    {
        var r = Evaluate([.. Many(40, 400, 12), Spot(120, 1, 2), Spot(140, 3, 4)]);
        var near = r.Forty!.At(100)!;
        Assert.Equal((PathVerdict.Closed, PskEvidence.OtherDistances, 2, 4), (near.Verdict, near.ClosedBy, near.Spots, near.Stations));
        // Three spots is neither open nor closed.
        var three = Evaluate([.. Many(40, 400, 12), Spot(120, 1, 2), Spot(140, 3, 4), Spot(160, 5, 6)]);
        Assert.Equal(PathVerdict.NoData, three.At100);
    }

    [Fact]
    public void ShortPaths_AreClosed_When80mShortPathsAreBusy_And40mIsQuietThere()
    {
        var r = Evaluate([.. Many(10, 120, 6, PskBand.Eighty), Spot(400, 1, 2)]);
        var near = r.Forty!.At(100)!;
        Assert.Equal((PathVerdict.Closed, PskEvidence.EightyMetres), (near.Verdict, near.ClosedBy));
        Assert.Equal(IonoState.Poor, r.State);
        Assert.Equal("Closed", r.Headline());
        Assert.Equal(PathVerdict.Open, r.Eighty!.At(100)!.Verdict);
        // 80 m itself is never judged closed by 40 m.
        Assert.Equal(PathVerdict.NoData, Evaluate(Many(10, 120, 6)).Eighty!.At(100)!.Verdict);
        // Not busy enough on 80 m: no data.
        Assert.Equal(PathVerdict.NoData, Evaluate(Many(9, 120, 6, PskBand.Eighty)).At100);
        Assert.Equal(PathVerdict.NoData, Evaluate(Many(10, 120, 5, PskBand.Eighty)).At100);
    }

    [Fact]
    public void Like20261006_NothingUnder250Km_ButOpenFurtherOut_GivesTheSkipZone()
    {
        // Nothing within about 220 km heard GB7RDG, while 500 to 800 km paths worked well.
        var spots = new List<PskSpot>();
        spots.AddRange(Many(25, 300, 12, snr: -14));
        spots.AddRange(Many(60, 520, 20, snr: -9));
        spots.AddRange(Many(35, 760, 14, snr: -12));
        var r = Evaluate(spots);
        Assert.Equal((PathVerdict.Closed, PathVerdict.Open, PathVerdict.Open), (r.At100, r.At500, r.At1000));
        Assert.Equal(IonoState.Marginal, r.State);
        // The 5th percentile of 120 distances is the 7th shortest: 300 to 306 km, so about 300.
        Assert.Equal(300, r.SkipZoneKm);
        Assert.Equal("Open from about 300 km", r.Headline());
        var far = r.Forty!.At(500)!;
        Assert.Equal((85, -9), (far.Spots, far.SnrMedianDb));
        Assert.Equal(
            "PSK Reporter (last 30 min, 40 m FT8/FT4/WSPR): open at 500 km (85 spots, 20 stations) and 1000 km (35 spots, 14 stations), nothing under 250 km despite 120 spots further out.",
            r.Summary(Now));
        Assert.Equal("100 km closed (0 spots), 500 km open (85 spots, 20 stations, median -9 dB), 1000 km open (35 spots, 14 stations, median -11 dB)", PskReading.Distances(r.Forty));

        // Open close in too: no skip zone, GOOD.
        var good = Evaluate([.. spots, .. Many(8, 90, 6)]);
        Assert.Equal((IonoState.Good, 0), (good.State, good.SkipZoneKm));
        Assert.Equal("Open near and far", good.Headline());
    }

    [Fact]
    public void OnlySpotsInTheWindow_Count_EachOnce()
    {
        var old = Spot(400, 1, 2, minutesAgo: 31);
        var fresh = Spot(400, 1, 2, minutesAgo: 29);
        var ahead = Spot(400, 1, 2, minutesAgo: -6);
        var r = Evaluate([old, fresh, fresh, ahead]);
        Assert.Equal(1, r.Forty!.At(500)!.Spots);
    }

    [Fact]
    public void AFeedThatWasDown_SaysUnknown_ButKeepsTheCounts()
    {
        var spots = Many(60, 520, 20).ToList();
        var shortUp = Evaluate(spots, up: TimeSpan.FromMinutes(19));
        Assert.True(shortUp.FeedDown);
        Assert.Equal((IonoState.Unknown, PathVerdict.NoData), (shortUp.State, shortUp.At500));
        Assert.Equal(60, shortUp.Forty!.At(500)!.Spots);
        Assert.Equal("No verdict: the feed was down", shortUp.Headline());
        Assert.Contains("the feed was down for too much of that time", shortUp.Summary(Now), StringComparison.Ordinal);
        Assert.Contains("500 km not judged (60 spots", shortUp.JournalLine(Now), StringComparison.Ordinal);

        Assert.True(Evaluate(spots, connected: false).FeedDown);
        Assert.False(Evaluate(spots, up: TimeSpan.FromMinutes(20)).FeedDown);
    }

    [Fact]
    public void Medians_NeedFiveSnrs()
    {
        Assert.Null(PskEvaluator.Median([1, 2, 3, 4]));
        Assert.Equal(3, PskEvaluator.Median([5, 1, 3, 2, 4]));
        // An even count: the middle two are -13 and -12, and -12.5 rounds away from zero.
        Assert.Equal(-13, PskEvaluator.Median([-10, -11, -12, -13, -14, -15]));
    }

    [Fact]
    public void TheJournalLine_HasBothBands_TheCountsAndTheSkipZones()
    {
        var r = Evaluate([.. Many(60, 520, 20), .. Many(12, 120, 8, PskBand.Eighty)]);
        string line = r.JournalLine(Now.AddMinutes(1));
        Assert.StartsWith("pskreporter: MARGINAL", line, StringComparison.Ordinal);
        Assert.Contains("last 30 min to 14:00Z (1 min old)", line, StringComparison.Ordinal);
        Assert.Contains("40 m 100 km closed (0 spots), 500 km open (60 spots, 20 stations, median -9 dB)", line, StringComparison.Ordinal);
        Assert.Contains("80 m (not sent) 100 km open (12 spots, 8 stations, median -9 dB)", line, StringComparison.Ordinal);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void TheRecord_RoundTrips_In27Octets_OneFrameOf59_EitherOfTwoRebuildingIt()
    {
        var spots = new List<PskSpot>();
        spots.AddRange(Many(60, 520, 20, snr: -9));
        spots.AddRange(Many(35, 760, 14, snr: -12));
        spots.Add(Spot(120, 1, 2, snr: null));
        spots.AddRange(Many(12, 120, 8, PskBand.Eighty));
        var r = Evaluate(spots);
        byte[] bytes = PskRecord.Encode(r);
        Assert.Equal(27, bytes.Length);
        Assert.Equal((byte)ObjectKind.Propagation, bytes[0]);
        Assert.Equal(3, bytes[2]);

        Assert.True(PskRecord.TryDecode(bytes, out var back));
        Assert.Equal(r.ObservedUtc, back.ObservedUtc);
        Assert.Equal((r.State, r.At100, r.At500, r.At1000, r.SkipZoneKm), (back.State, back.At100, back.At500, back.At1000, back.SkipZoneKm));
        Assert.Equal(r.Forty!.Bins, back.Forty!.Bins);
        Assert.Equal(PskEvidence.OtherDistances, back.Forty.At(100)!.ClosedBy);
        Assert.Null(back.Eighty); // 80 m is not sent
        Assert.Equal(30, back.WindowMinutes);
        Assert.Equal(r.Summary(Now), back.Summary(Now));

        // Not an ionosonde: the ionosonde's reader leaves it alone, and the ionosonde's record is not this.
        Assert.False(IonoRecord.TryDecode(bytes, out _));
        var iono = IonoEvaluator.Evaluate([new IonoSounding("RL052", Now, IonoSource.Giro, 6.05, M3000: 3.3)], new IonoSettings(), Now);
        Assert.False(PskRecord.TryDecode(IonoRecord.Encode(iono), out _));

        var obj = PskRecord.ToTransferObject(r);
        Assert.Equal(28, obj.Frame(0).Symbol.Length);
        Assert.Equal(59, obj.Frame(0).ToBytes().Length);
        foreach (uint esi in new uint[] { 0, 1 })
        {
            var frame = obj.Frame(esi);
            var decoder = new ObjectDecoder(frame.Oti);
            decoder.Add(new PayloadId(0, esi), frame.Symbol.Span);
            Assert.Equal(obj.Bytes.ToArray(), decoder.TryDecode());
        }
    }

    [Fact]
    public void TheRecord_SaysFeedDown_AndCapsWhatDoesNotFit()
    {
        var down = Evaluate(Many(70_000, 520, 20).Take(66_000), up: TimeSpan.Zero);
        Assert.True(PskRecord.TryDecode(PskRecord.Encode(down), out var back));
        Assert.True(back.FeedDown);
        Assert.Equal(IonoState.Unknown, back.State);
        Assert.Equal(ushort.MaxValue, back.Forty!.At(500)!.Spots);
        Assert.Null(back.SkipZoneKm);
        Assert.Throws<ArgumentException>(() => PskRecord.Encode(PskReading.None));
    }

    [Fact]
    public void Receivers_KeepTheNewest_AcrossRestarts_BesideTheIonosonde_NeverForTheBbs()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var r = Evaluate([.. Many(60, 520, 20), .. Many(40, 760, 12)]);
        var newer = PskRecord.ToTransferObject(r);
        var older = PskRecord.ToTransferObject(PskEvaluator.Evaluate(Many(60, 520, 20), Now.AddMinutes(-60), FullWindow));
        var iono = IonoRecord.ToTransferObject(IonoEvaluator.Evaluate([new IonoSounding("RL052", Now, IonoSource.Giro, 6.05, M3000: 3.3)], new IonoSettings(), Now));

        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });
        var result = store.Accept(newer.Frame(1).ToBytes());
        Assert.Equal(FrameOutcome.CompletedPskReporter, result.Outcome);
        Assert.Equal(r.Forty!.Bins, result.PskReporter!.Forty!.Bins);
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(newer.Frame(0).ToBytes()).Outcome);
        Assert.Equal(FrameOutcome.CompletedPskReporter, store.Accept(older.Frame(0).ToBytes()).Outcome);
        Assert.Equal(r.ObservedUtc, store.PskReporter!.ObservedUtc); // the older does not replace the newer
        Assert.Equal(FrameOutcome.CompletedIonosphere, store.Accept(iono.Frame(0).ToBytes()).Outcome);
        Assert.Empty(store.Pending());
        Assert.Empty(log);

        var reopened = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        Assert.Equal(r.ObservedUtc, reopened.PskReporter!.ObservedUtc);
        Assert.Equal(6.05, reopened.Ionosphere!.FoF2);
    }

    [Fact]
    public void AReceiverBeforeIt_SeesOnlyAnotherSourceOfTypeFour()
    {
        // v0.6's reader: the common part's length is there, the source is not one it knows, so
        // the object is done with quietly (CompletedUnknown), as an unknown source always was.
        var r = Evaluate(Many(60, 520, 20));
        byte[] bytes = PskRecord.Encode(r);
        Assert.True(bytes.Length >= 1 + IonoRecord.CommonLength);
        Assert.False(IonoRecord.TryDecode(bytes, out _));
        Assert.Equal(1, bytes[1]); // record version 1, as the ionosonde's
    }
}
