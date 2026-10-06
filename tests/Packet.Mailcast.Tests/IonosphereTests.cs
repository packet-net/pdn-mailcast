using Mailcast.RaptorQ;
using Packet.Mailcast.Propagation;

namespace Packet.Mailcast.Tests;

/// <summary>
/// The ionosonde reading: judging soundings, reading GIRO's and PROPquest's real answers,
/// content type 4 on the air and at receivers old and new, and its frames inside the budget.
/// </summary>
public class IonosphereTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 14, 42, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sounded = new(2026, 10, 6, 14, 30, 0, TimeSpan.Zero);
    private static readonly IonoSettings Settings = new();

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Propagation", name));

    private static IonoSounding Chilton(double? foF2, double? m3000 = null, double? mufd100 = null, double? mufd500 = null, double? mufd1000 = null, DateTimeOffset? at = null) =>
        new("RL052", at ?? Sounded, IonoSource.Giro, foF2, M3000: m3000, Mufd100: mufd100, Mufd500: mufd500, Mufd1000: mufd1000);

    [Fact]
    public void FoF2Of605_IsPoor_WhenEvenTheMufAt1000KmIsBelow71()
    {
        var r = IonoEvaluator.Evaluate([Chilton(6.05, mufd100: 6.08, mufd500: 6.6, mufd1000: 7.0)], Settings, Now);
        Assert.Equal(IonoState.Poor, r.State);
        Assert.Equal((PathVerdict.Closed, PathVerdict.Closed, PathVerdict.Closed), (r.At100, r.At500, r.At1000));
        Assert.Null(r.SkipZoneKm);
        Assert.Equal(MufMethod.Measured, r.Method);
        Assert.Equal("40 m: closed at 100, 500 and 1000 km", r.Band());
    }

    [Fact]
    public void FoF2Of605_IsMarginal_WhenTheMufAt1000KmReaches71_WithTheSkipZoneInterpolated()
    {
        var r = IonoEvaluator.Evaluate([Chilton(6.05, mufd100: 6.08, mufd500: 6.6, mufd1000: 8.6)], Settings, Now);
        Assert.Equal(IonoState.Marginal, r.State);
        Assert.Equal((PathVerdict.Closed, PathVerdict.Closed, PathVerdict.Reliable), (r.At100, r.At500, r.At1000));
        // 500 + (7.1 - 6.6) / (8.6 - 6.6) x 500 = 625, to the nearest 10 km.
        Assert.Equal(630, r.SkipZoneKm);
        Assert.Equal(
            "Ionosphere: Chilton foF2 6.05 MHz at 14:30 UTC (12 min old). 40 m: closed at 100 km, open from about 630 km.",
            r.Describe(Now));
        Assert.Equal("100 km closed, 500 km closed, 1000 km good", r.Distances());
    }

    [Fact]
    public void FoF2Of605_WithoutMufds_IsEstimatedByP533_AndIsMarginalLikeOn20261006()
    {
        // The day's peak: nothing heard within 221 km, bulletins rebuilt at 502 and 534 km.
        var r = IonoEvaluator.Evaluate([Chilton(6.05, m3000: 3.3)], Settings, Now);
        Assert.Equal(IonoState.Marginal, r.State);
        Assert.Equal(MufMethod.Estimated, r.Method);
        Assert.Equal(6.651, r.Mufd100!.Value, 0.001);
        Assert.Equal(8.211, r.Mufd500!.Value, 0.001);
        Assert.Equal(11.643, r.Mufd1000!.Value, 0.001);
        Assert.Equal((PathVerdict.Closed, PathVerdict.Open, PathVerdict.Reliable), (r.At100, r.At500, r.At1000));
        // Found along the curve, not between the three points: the MUF reaches 7.1 MHz at 280 km.
        Assert.Equal(280, r.SkipZoneKm);
        Assert.Equal("Open from about 280 km", r.Headline());
    }

    [Fact]
    public void FoF2Of9_IsGood()
    {
        var r = IonoEvaluator.Evaluate([Chilton(9.0, m3000: 3.0)], Settings, Now);
        Assert.Equal(IonoState.Good, r.State);
        Assert.Equal((PathVerdict.Reliable, PathVerdict.Reliable, PathVerdict.Reliable), (r.At100, r.At500, r.At1000));
        Assert.Equal(0, r.SkipZoneKm);
        Assert.Equal("40 m: open from 0 km out to 1000 km", r.Band());
        Assert.Equal("Open near and far", r.Headline());
    }

    [Fact]
    public void FoF2Of75_IsMarginal_OpenButNotReliableClose_In()
    {
        var r = IonoEvaluator.Evaluate([Chilton(7.5, m3000: 3.1)], Settings, Now);
        Assert.Equal(IonoState.Marginal, r.State);
        Assert.Equal(PathVerdict.Open, r.At100);
        Assert.Equal(0, r.SkipZoneKm);
        Assert.Equal("Open, but only just", r.Headline());
    }

    [Fact]
    public void AStaleSounding_IsUnknown_WithItsLastValuesAndAge_NeverExtrapolated()
    {
        var old = Chilton(9.0, m3000: 3.0, at: Now.AddMinutes(-46));
        var r = IonoEvaluator.Evaluate([old], Settings, Now);
        Assert.Equal(IonoState.Unknown, r.State);
        Assert.Equal(9.0, r.FoF2);
        Assert.Equal(46, r.AgeMinutes);
        Assert.Equal("Ionosphere: no fresh reading. Last: Chilton foF2 9.00 MHz at 13:56 UTC (46 min old). Too old to judge 40 m by.", r.Describe(Now));
        Assert.Equal("Chilton foF2 9.00 MHz at 13:56 UTC (46 min old). Too old to judge 40 m by.", r.Summary(Now));
        Assert.Equal("No fresh reading", r.Headline());

        // 45 minutes is still fresh, and a fresh reading goes UNKNOWN as it ages.
        var fresh = IonoEvaluator.Evaluate([old], Settings, Now.AddMinutes(-1));
        Assert.Equal(IonoState.Good, fresh.State);
        Assert.Equal(IonoState.Unknown, fresh.AsOf(Now, Settings.StaleAfter).State);
        Assert.Equal(46, fresh.AsOf(Now, Settings.StaleAfter).AgeMinutes);
    }

    [Fact]
    public void AllNull_IsUnknown_AndNoSoundingIsNone()
    {
        var empty = Chilton(null);
        Assert.False(empty.Usable);
        var r = IonoEvaluator.Evaluate([empty], Settings, Now);
        Assert.Equal(IonoState.Unknown, r.State);
        Assert.False(r.HasSounding);
        Assert.Same(IonoReading.None, IonoEvaluator.Evaluate([], Settings, Now));
        Assert.Equal("Ionosphere: no reading yet.", r.Describe(Now));
    }

    [Fact]
    public void Stations_ChiltonFirst_ThenFairford_ThenDourbes_AndOnlyFreshOnesWinOnOrder()
    {
        var chilton = Chilton(6.0, m3000: 3.0, at: Now.AddMinutes(-30));
        var fairford = new IonoSounding("FF051", Now.AddMinutes(-5), IonoSource.PropQuest, 7.0, M3000: 3.0);
        var dourbes = new IonoSounding("DB049", Now.AddMinutes(-2), IonoSource.Giro, 8.0, M3000: 3.0);
        Assert.Equal("RL052", IonoEvaluator.Evaluate([dourbes, fairford, chilton], Settings, Now).Station);
        Assert.Equal("FF051", IonoEvaluator.Evaluate([dourbes, fairford, chilton with { Time = Now.AddMinutes(-50) }], Settings, Now).Station);
        Assert.Equal("DB049", IonoEvaluator.Evaluate([dourbes, fairford with { Time = Now.AddHours(-2) }], Settings, Now).Station);

        // None fresh: the newest, as UNKNOWN.
        var last = IonoEvaluator.Evaluate([chilton with { Time = Now.AddHours(-3) }, fairford with { Time = Now.AddHours(-2) }], Settings, Now);
        Assert.Equal(("FF051", IonoState.Unknown, 120), (last.Station, last.State, last.AgeMinutes));

        // A station not in the list is never used.
        Assert.False(IonoEvaluator.Evaluate([dourbes], Settings with { Stations = ["RL052"] }, Now).HasSounding);
    }

    [Fact]
    public void BasicMuf_IsItuRP533s()
    {
        // ITU-R P.533 section 3.5.1.1 (P.1240 section 3.1), worked by hand from its equations 3
        // to 6 with fH 1.2 MHz and x = 2: B = 3.3633, dmax 4000 km (capped), C(3000) = 0.81965.
        Assert.Equal(6.651, IonoEvaluator.EstimateMuf(6.05, 3.3, 100)!.Value, 0.001);
        Assert.Equal(8.211, IonoEvaluator.EstimateMuf(6.05, 3.3, 500)!.Value, 0.001);
        Assert.Equal(11.643, IonoEvaluator.EstimateMuf(6.05, 3.3, 1000)!.Value, 0.001);
        // The review's ITU-R P.533 figure for this sounding at 500 km: about 8.2 MHz.
        Assert.InRange(IonoEvaluator.EstimateMuf(6.05, 3.3, 500)!.Value, 8.1, 8.3);
        // Its ends: at 0 km C(D) is 0, so foF2 plus half the gyrofrequency; at 3000 km C(D)/C(3000)
        // is 1, so B x foF2 plus the x-wave term a quarter of the way to dmax.
        double b = 3.3 - 0.124 + ((3.3 * 3.3) - 4) * (0.0215 + (0.005 * Math.Sin((7.854 / 2) - 1.9635)));
        Assert.Equal(6.05 + 0.6, IonoEvaluator.EstimateMuf(6.05, 3.3, 0)!.Value, 6);
        Assert.Equal((b * 6.05) + (0.6 * 0.25), IonoEvaluator.EstimateMuf(6.05, 3.3, 3000)!.Value, 6);
        // Without M(3000)F2 only 100 km has a MUF, foF2 itself.
        Assert.Equal(6.0, IonoEvaluator.EstimateMuf(6.0, null, 100));
        Assert.Null(IonoEvaluator.EstimateMuf(6.0, null, 500));
        // M(3000) from MUF(3000) / foF2 when the source gives only those.
        var s = new IonoSounding("RL052", Sounded, IonoSource.PropQuest, 6.0, Muf3000: 19.2);
        Assert.Equal(IonoEvaluator.EstimateMuf(6.0, 3.2, 1000), IonoEvaluator.MufAt(s, 1000).Muf);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    [InlineData(1e300)]
    [InlineData(0.1)]
    [InlineData(51.0)]
    public void ImplausibleValues_AreNoValues_SoNothingNonFiniteComesOut(double bad)
    {
        var s = new IonoSounding("RL052", Sounded, IonoSource.Giro, bad, Muf3000: bad, M3000: bad, Mufd100: bad, Mufd500: bad, Mufd1000: bad);
        Assert.Equal((null, null, null, null, null, null), (s.FoF2, s.Muf3000, s.M3000, s.Mufd100, s.Mufd500, s.Mufd1000));
        Assert.False(s.Usable);
        Assert.False((s with { FoF2 = bad }).Usable);
        var r = new IonoReading { FoF2 = bad, Mufd100 = bad, Mufd500 = bad, Mufd1000 = bad };
        Assert.Equal((null, null, null, null), (r.FoF2, r.Mufd100, r.Mufd500, r.Mufd1000));
    }

    [Fact]
    public void AHugeM3000_IsRefused_RatherThanOverflowing()
    {
        // MUF(3000) 49 MHz over foF2 0.6 MHz would be M = 82: not believed, so no MUF beyond 100 km.
        var r = IonoEvaluator.Evaluate([new IonoSounding("RL052", Sounded, IonoSource.PropQuest, 0.6, Muf3000: 49)], Settings, Now);
        Assert.Null(r.Mufd500);
        Assert.Null(IonoEvaluator.EstimateMuf(6.0, 1e300, 1000));
        Assert.Null(IonoEvaluator.EstimateMuf(6.0, 6.5, 1000));
        Assert.NotNull(IonoEvaluator.EstimateMuf(6.0, 6.0, 1000));
        foreach (double? v in new[] { r.FoF2, r.Mufd100, r.Mufd500, r.Mufd1000 })
        {
            Assert.True(v is null || double.IsFinite(v.Value));
        }
    }

    [Fact]
    public void Band_SaysWhatIsOpen_EvenWithoutTheNearestDistance()
    {
        // 100 km unknown (a measured MUFD at 500 and 1000 only, and no foF2).
        var r = IonoEvaluator.Evaluate([new IonoSounding("RL052", Sounded, IonoSource.PropQuest, null, Mufd500: 7.5, Mufd1000: 9.5)], Settings, Now);
        Assert.Equal((PathVerdict.NoData, PathVerdict.Open, PathVerdict.Reliable), (r.At100, r.At500, r.At1000));
        Assert.Equal(IonoState.Marginal, r.State);
        Assert.Null(r.SkipZoneKm);
        Assert.Equal("40 m: open at 500 and 1000 km", r.Band());
        Assert.Equal("Open at some distances", r.Headline());
        var closed = IonoEvaluator.Evaluate([new IonoSounding("RL052", Sounded, IonoSource.PropQuest, null, Mufd500: 6.5, Mufd1000: 6.9)], Settings, Now);
        Assert.Equal("40 m: closed at 500 and 1000 km, no MUF for the rest", closed.Band());
    }

    [Fact]
    public void Giro_RealAnswer_ReadsFoF2MufAndM3000()
    {
        var soundings = GiroParser.Parse(Fixture("giro-FF051-2026-10-06.txt"));
        Assert.Equal(7, soundings.Count);
        var first = soundings[0];
        Assert.Equal(("FF051", new DateTimeOffset(2026, 10, 6, 12, 30, 0, TimeSpan.Zero), IonoSource.Giro), (first.Station, first.Time, first.Source));
        Assert.Equal((5.713, 18.563, 3.26), (first.FoF2!.Value, first.Muf3000!.Value, first.M3000!.Value));
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 13, 15, 0, TimeSpan.Zero), soundings[^1].Time);

        // As judged just after the last sounding: Fairford, estimated, open only far out.
        var r = IonoEvaluator.Evaluate(soundings, Settings, new DateTimeOffset(2026, 10, 6, 13, 20, 0, TimeSpan.Zero));
        Assert.Equal(("FF051", IonoState.Marginal, MufMethod.Estimated, PathVerdict.Closed, PathVerdict.Open), (r.Station, r.State, r.Method, r.At100, r.At500));
        // And at the moment it was fetched, four hours on: UNKNOWN.
        Assert.Equal(IonoState.Unknown, IonoEvaluator.Evaluate(soundings, Settings, new DateTimeOffset(2026, 10, 6, 17, 24, 0, TimeSpan.Zero)).State);
    }

    [Fact]
    public void Giro_NoDataAnswer_IsNoSoundings_AndLowConfidenceIsDropped()
    {
        Assert.Empty(GiroParser.Parse(Fixture("giro-RL052-2026-10-06-no-data.txt")));
        Assert.Throws<FormatException>(() => GiroParser.Parse("<html>404</html>"));

        // The real answer with its confidence scores changed: under 50 goes, 999 (manual) and 50 stay.
        string text = Fixture("giro-FF051-2026-10-06.txt")
            .Replace("2026-10-06T12:30:00.000Z  90", "2026-10-06T12:30:00.000Z  49", StringComparison.Ordinal)
            .Replace("2026-10-06T12:37:30.000Z  95", "2026-10-06T12:37:30.000Z  -1", StringComparison.Ordinal)
            .Replace("2026-10-06T12:45:00.000Z  95", "2026-10-06T12:45:00.000Z 999", StringComparison.Ordinal)
            .Replace("2026-10-06T12:52:30.000Z 100", "2026-10-06T12:52:30.000Z  50", StringComparison.Ordinal)
            .Replace("2026-10-06T13:00:00.000Z  95  5.713", "2026-10-06T13:00:00.000Z  95    ---", StringComparison.Ordinal);
        var kept = GiroParser.Parse(text);
        Assert.Equal(4, kept.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 45, 0, TimeSpan.Zero), kept[0].Time);
        Assert.DoesNotContain(kept, s => s.Time == new DateTimeOffset(2026, 10, 6, 13, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Giro_RefusesAWebPage_AndSkipsLinesThatDoNotFitTheColumns_AndNonFiniteValues()
    {
        Assert.Throws<FormatException>(() => GiroParser.Parse("<html><title>GIRO</title><p># DIDBase is down</p></html>"));
        Assert.Throws<FormatException>(() => GiroParser.Parse("# Global Ionospheric Radio Observatory (GIRO)\n# nothing else\n"));

        string text = Fixture("giro-FF051-2026-10-06.txt")
            .Replace("2026-10-06T12:30:00.000Z  90  5.713 // 18.563 // 3.26 //  6.43 //", "2026-10-06T12:30:00.000Z  90  5.713 // 18.563 //", StringComparison.Ordinal)
            .Replace("2026-10-06T12:37:30.000Z  95  5.663", "2026-10-06T12:37:30.000Z  95 Infinity", StringComparison.Ordinal)
            .Replace("2026-10-06T12:45:00.000Z  95  5.613 // 18.527 // 3.31", "2026-10-06T12:45:00.000Z  95  5.613 // 1e999 // NaN", StringComparison.Ordinal);
        var soundings = GiroParser.Parse(text);
        Assert.DoesNotContain(soundings, s => s.Time == new DateTimeOffset(2026, 10, 6, 12, 30, 0, TimeSpan.Zero));
        // An infinite foF2 is no foF2, and with no foF2 the sounding says nothing about 40 m.
        Assert.DoesNotContain(soundings, s => s.Time == new DateTimeOffset(2026, 10, 6, 12, 37, 30, TimeSpan.Zero));
        var huge = soundings.Single(s => s.Time == new DateTimeOffset(2026, 10, 6, 12, 45, 0, TimeSpan.Zero));
        Assert.Equal((5.613, null, null), (huge.FoF2!.Value, huge.Muf3000, huge.M3000));
    }

    [Fact]
    public void PropQuest_NonFiniteValues_AreMissing()
    {
        string json = """
            {"Observations":[{"DATE_TODAY":"2026-10-06,2026-10-06,2026-10-06","TIME_TODAY":"12:00,12:05,12:10",
            "foF2_TODAY":"Infinity,1e999,6.1","MUFD3000_TODAY":"20,20,NaN","MUFD1000_TODAY":"null,null,-Infinity",
            "MUFD500_TODAY":"null,null,null","MUFD100_TODAY":"null,null,null","OBSERVATORY_TODAY":"RL052,RL052,RL052"}]}
            """;
        var s = Assert.Single(PropQuestParser.Parse(json));
        Assert.Equal((6.1, null, null), (s.FoF2!.Value, s.Muf3000, s.Mufd1000));
    }

    [Fact]
    public void FrameOutcomes_KeepTheirOldNumbers()
    {
        // As in every release before the reading: a new outcome only ever goes at the end.
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
            new[]
            {
                FrameOutcome.NotAFrame, FrameOutcome.AlreadyComplete, FrameOutcome.UnknownDictionary, FrameOutcome.Duplicate,
                FrameOutcome.Stored, FrameOutcome.CompletedBulletin, FrameOutcome.CompletedDirectory, FrameOutcome.CompletedUnhandled,
                FrameOutcome.CompletedUnknown, FrameOutcome.Rejected, FrameOutcome.CompletedIonosphere,
            }.Select(o => (int)o));
    }

    [Fact]
    public void ExtraObjects_KeepTheirNextEsi_SoTheSameObjectNeverRepeatsOne()
    {
        using var dir = new TempDirectory();
        var slot = new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);
        var store = new HeadEndStore(dir.Path, Compression.Default, Budgeted);
        store.Offer(TestBulletins.Make(1, 3000), new DateOnly(2026, 10, 6));
        var obj = IonoRecord.ToTransferObject(Sample());
        var plan = BroadcastScheduler.Plan(store.InRotation(slot), slot, 1, Compression.Default, Budgeted, store.DirectoryNextEsi, Frames(20), null, [obj.Frame(0), obj.Frame(1)]);
        Assert.Equal([obj.ObjectId], plan.ExtraObjects);
        Assert.Equal(0u, store.DirectoryNextEsi(obj.ObjectId));
        store.Commit(plan);
        Assert.Equal(2u, store.DirectoryNextEsi(obj.ObjectId));
        Assert.Equal(2u, new HeadEndStore(dir.Path, Compression.Default, Budgeted).DirectoryNextEsi(obj.ObjectId)); // across a restart
    }

    [Fact]
    public void PropQuest_RealAnswer_ReadsBothDays_AndNullsAreMissing()
    {
        var soundings = PropQuestParser.Parse(Fixture("propquest-RL052-FF051-2026-10-05-06.json"));
        Assert.All(soundings, s => Assert.Equal(("FF051", IonoSource.PropQuest), (s.Station, s.Source)));
        Assert.Contains(soundings, s => s.Time.Date == new DateTime(2026, 10, 5));
        Assert.Equal(soundings.Count, soundings.Select(s => s.Time).Distinct().Count());
        var peak = soundings.Single(s => s.Time == new DateTimeOffset(2026, 10, 6, 11, 30, 0, TimeSpan.Zero));
        Assert.Equal((6.05, 19.954), (peak.FoF2!.Value, peak.Muf3000!.Value));
        Assert.Null(peak.Mufd100);
        var last = soundings[^1];
        Assert.Equal((new DateTimeOffset(2026, 10, 6, 13, 0, 0, TimeSpan.Zero), 5.7), (last.Time, last.FoF2!.Value));

        // At 11:35 the peak is the reading: estimated, open only from about 280 km.
        var r = IonoEvaluator.Evaluate(soundings, Settings, new DateTimeOffset(2026, 10, 6, 11, 35, 0, TimeSpan.Zero));
        Assert.Equal(("FF051", IonoState.Marginal, IonoSource.PropQuest, MufMethod.Estimated), (r.Station, r.State, r.Source, r.Method));
        Assert.Equal(280, r.SkipZoneKm);
        Assert.Throws<FormatException>(() => PropQuestParser.Parse("{}"));
        Assert.Throws<FormatException>(() => PropQuestParser.Parse("not json"));
    }

    private static IonoReading Sample() => IonoEvaluator.Evaluate([Chilton(6.05, m3000: 3.3)], Settings, Now);

    [Fact]
    public void Record_Is24Octets_AndRoundTrips()
    {
        var reading = Sample();
        byte[] bytes = IonoRecord.Encode(reading);
        Assert.Equal(24, bytes.Length);
        Assert.Equal(4, bytes[0]);
        Assert.True(IonoRecord.TryDecode(bytes, out var back));
        Assert.Equal(reading.State, back.State);
        Assert.Equal(reading.Station, back.Station);
        Assert.Equal(reading.SoundingTimeUtc, back.SoundingTimeUtc);
        Assert.Equal(6.05, back.FoF2);
        Assert.Equal(Math.Round(reading.Mufd500!.Value, 2), back.Mufd500);
        Assert.Equal(Math.Round(reading.Mufd1000!.Value, 2), back.Mufd1000);
        Assert.Equal(reading.SkipZoneKm, back.SkipZoneKm);
        Assert.Equal((reading.Source, reading.Method, reading.AgeMinutes), (back.Source, back.Method, back.AgeMinutes));
        Assert.Equal((reading.At100, reading.At500, reading.At1000), (back.At100, back.At500, back.At1000));
        Assert.Equal(reading.Describe(Now), back.Describe(Now));

        // Nothing at all: no foF2, no MUF, no skip zone.
        var bare = new IonoReading { Station = "DB049", SoundingTimeUtc = Sounded, Source = IonoSource.Giro };
        Assert.True(IonoRecord.TryDecode(IonoRecord.Encode(bare), out var bareBack));
        Assert.Equal((null, null, null, null, null), (bareBack.FoF2, bareBack.Mufd100, bareBack.Mufd500, bareBack.Mufd1000, bareBack.SkipZoneKm));
        Assert.Throws<ArgumentException>(() => IonoRecord.Encode(IonoReading.None));
        Assert.Throws<ArgumentException>(() => IonoRecord.Encode(reading with { Source = IonoSource.None }));

        // A later version's longer record is read as far as this one goes; a cut-short one is not.
        Assert.True(IonoRecord.TryDecode([.. bytes, 7, 7], out _));
        Assert.False(IonoRecord.TryDecode(bytes.AsSpan(0, 23), out _));

        // Another source (PSK Reporter spots, say, one day) shares the common part; this version
        // does not read it as an ionosonde, and a receiver ignores it quietly.
        byte[] spots = [.. bytes.AsSpan(0, 1 + IonoRecord.CommonLength)];
        spots[2] = 3;
        Assert.False(IonoRecord.TryDecode(spots, out _));
    }

    [Fact]
    public void Object_IsOneSmallSymbol_AndEitherFrameAloneRebuildsIt()
    {
        var obj = IonoRecord.ToTransferObject(Sample());
        Assert.Equal((1, 24, 24L, (ushort)0), (obj.SourceSymbols, obj.Oti.SymbolSize, obj.Length, obj.DictionaryId));
        Assert.Equal(ObjectId.Of(0, obj.Bytes), obj.ObjectId);
        foreach (uint esi in new uint[] { 0, 1 })
        {
            var payload = obj.Frame(esi).ToBytes();
            Assert.InRange(payload.Length, 50, MailcastFrame.Overhead + 24);
            Assert.True(MailcastFrame.TryParse(payload, out var frame));
            var decoder = new ObjectDecoder(frame!.Oti);
            decoder.Add(new PayloadId(0, frame.EncodingSymbolId), frame.Symbol.Span);
            Assert.Equal(obj.Bytes.ToArray(), decoder.TryDecode());
        }
        // ESI 0 is the object itself: 55 octets a frame, 71 as AX.25.
        Assert.Equal(24, obj.Frame(0).Symbol.Length);
        Assert.Equal(55, obj.Frame(0).ToBytes().Length);
    }

    [Fact]
    public void Receiver_KeepsTheReadingOutOfTheBbs_IgnoresItsLaterFrames_AndRemembersTheNewestAcrossRestarts()
    {
        using var dir = new TempDirectory();
        var log = new List<string>();
        var newer = IonoRecord.ToTransferObject(Sample());
        var older = IonoRecord.ToTransferObject(Sample() with { SoundingTimeUtc = Sounded.AddMinutes(-15), FoF2 = 5.9 });

        var store = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast with { Log = log.Add });
        var result = store.Accept(newer.Frame(1).ToBytes());
        Assert.Equal(FrameOutcome.CompletedIonosphere, result.Outcome);
        Assert.Equal(6.05, result.Ionosphere!.FoF2);
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(newer.Frame(0).ToBytes()).Outcome);
        Assert.Equal(FrameOutcome.CompletedIonosphere, store.Accept(older.Frame(0).ToBytes()).Outcome);
        Assert.Equal(6.05, store.Ionosphere!.FoF2); // the older sounding does not replace the newer
        // A source this version does not know: ignored quietly, marked done, the reading kept.
        byte[] other = [.. IonoRecord.Encode(Sample()).AsSpan(1, IonoRecord.CommonLength)];
        other[1] = 3;
        var spots = TransferObject.ForRecord((byte)ObjectKind.Propagation, other);
        Assert.Equal(FrameOutcome.CompletedUnknown, store.Accept(spots.Frame(0).ToBytes()).Outcome);
        Assert.Equal(FrameOutcome.AlreadyComplete, store.Accept(spots.Frame(1).ToBytes()).Outcome);
        Assert.Equal(6.05, store.Ionosphere!.FoF2);

        Assert.Empty(store.Pending());
        Assert.Equal(0, store.PartialObjects);
        Assert.Empty(log);

        var reopened = new ReceiverStore(dir.Path, Compression.Default, TestStores.Fast);
        Assert.Equal(Sounded, reopened.Ionosphere!.SoundingTimeUtc);
        Assert.Equal(FrameOutcome.AlreadyComplete, reopened.Accept(newer.Frame(1).ToBytes()).Outcome);
    }

    [Fact]
    public void OldReceivers_DropTypeFour_NeverForTheBbs()
    {
        var obj = IonoRecord.ToTransferObject(Sample());

        // v0.1.0 to v0.2.0 accept only kinds 1 and 2: the object is unusable, so dropped (with a
        // log line for each of its frames heard), never decompressed and never in the outbox.
        Assert.Throws<InvalidDataException>(() => V020Reader.Unpack(obj.Bytes, obj.DictionaryId, Compression.Default));

        // v0.3.0 to v0.5.2 (and pdn-soundmodem's receiver, on 0.4.3) knew types 1 to 3: anything
        // else completes as an unknown type, is marked done and is ignored without a word.
        Assert.True(ContentType.TryRead(obj.Bytes, out byte type, out _, out _));
        Assert.Equal(V052Reader.Outcome.Unknown, V052Reader.Classify(type));

        // Dictionary 0 is one every version knows, so no "unknown dictionary" line either.
        Assert.True(Compression.Default.Knows(obj.DictionaryId));
    }

    private static SlotBudget Frames(int frames) => new(TimeSpan.FromSeconds(frames), lengths => TimeSpan.FromSeconds(lengths.Count));

    private static readonly ScheduleOptions Budgeted = ScheduleOptions.HourlyBudget with { SymbolSize = 240 };

    private static CarriedBulletin Held(int seed, int size)
    {
        var b = TestBulletins.Make(seed, size);
        var transfer = TransferObject.ForBulletin(b, Budgeted.DictionaryId, Compression.Default, Budgeted.SymbolSize, Budgeted.Alignment);
        return new CarriedBulletin(b.Bid, b.Title, b.Serialize().Length, new DateOnly(2026, 10, 5), transfer, 0);
    }

    [Fact]
    public void Budget_CountsTheReadingsFrames_SoTheyTakeTheRoomOfBulletinFrames_AndNeverRunOver()
    {
        var slot = new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);
        CarriedBulletin[] carried = [Held(1, 9000), Held(2, 9000)];
        var extras = new[] { IonoRecord.ToTransferObject(Sample()).Frame(0), IonoRecord.ToTransferObject(Sample()).Frame(1) };
        var without = BroadcastScheduler.Plan(carried, slot, 1, Compression.Default, Budgeted, null, Frames(20), null);
        var with = BroadcastScheduler.Plan(carried, slot, 1, Compression.Default, Budgeted, null, Frames(20), null, extras);
        Assert.Equal(20, without.Frames.Count);
        Assert.Equal(20, with.Frames.Count);
        Assert.Equal(2, with.ExtraFrames);
        Assert.Equal(without.BulletinFrames - 2, with.BulletinFrames);
        Assert.Equal(2, with.Frames.Count(f => f.DictionaryId == 0 && f.Oti.TransferLength == IonoRecord.ObjectLength));
        // Never first (the directory is), and not in the directory.
        Assert.NotEqual(IonoRecord.ObjectLength, with.Frames[0].Oti.TransferLength);
        Assert.Equal(2, with.Directory.Entries.Count);
        Assert.DoesNotContain(with.Directory.Entries, e => e.ObjectId == extras[0].ObjectId);

        // With real airtime: the slot with the reading stays inside the same budget.
        var limit = TimeSpan.FromMinutes(7.5);
        Func<IReadOnlyList<int>, TimeSpan> airtime = lengths => TimeSpan.FromSeconds(30 + lengths.Sum(l => 0.8 + (l * 8 / 1200.0)));
        var real = BroadcastScheduler.Plan(carried, slot, 1, Compression.Default, Budgeted, null, new SlotBudget(limit, airtime), null, extras);
        Assert.Equal(2, real.ExtraFrames);
        Assert.True(airtime([.. real.Frames.Select(f => f.ToBytes().Length)]) <= limit);
    }

    [Fact]
    public void Reading_NeverMakesASlotKey_NorStopsOneKeying()
    {
        var slot = new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);
        var extras = new[] { IonoRecord.ToTransferObject(Sample()).Frame(0), IonoRecord.ToTransferObject(Sample()).Frame(1) };

        // Nothing in rotation: no bulletin frames, so a scheduled slot keys nothing, reading or not
        // (the planner sends a plan without bulletin frames only for a one-off slot).
        var empty = BroadcastScheduler.Plan([], slot, 1, Compression.Default, Budgeted, null, Frames(20), null, extras);
        Assert.Equal(0, empty.BulletinFrames);

        // Room for the directory and one bulletin frame only: the bulletin frame goes, the reading does not.
        var carried = new[] { Held(1, 9000) };
        var bare = BroadcastScheduler.Plan(carried, slot, 1, Compression.Default, Budgeted, null, Frames(20), null);
        int directory = bare.Objects[0].Count;
        var tight = BroadcastScheduler.Plan(carried, slot, 1, Compression.Default, Budgeted, null, Frames(directory + 1), null, extras);
        Assert.Equal((1, 0), (tight.BulletinFrames, tight.ExtraFrames));
    }
}

/// <summary>How v0.3.0 to v0.5.2 handled a rebuilt object by its type, copied from their ReceiverStore.Finish and ContentType.IsKnown.</summary>
internal static class V052Reader
{
    public enum Outcome
    {
        Bulletin,
        Directory,
        Unhandled,
        Unknown,
    }

    /// <summary>Types 1 and 2 were handled, 3 known and kept out of the BBS, anything else marked done as unknown.</summary>
    public static Outcome Classify(byte type) => type switch
    {
        1 => Outcome.Bulletin,
        2 => Outcome.Directory,
        3 => Outcome.Unhandled,
        _ => Outcome.Unknown,
    };
}
