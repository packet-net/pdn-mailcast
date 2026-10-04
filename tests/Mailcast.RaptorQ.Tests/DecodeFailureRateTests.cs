using Xunit.Abstractions;

namespace Mailcast.RaptorQ.Tests;

/// <summary>
/// Decoding from exactly K, K + 1 and K + 2 symbols with ESIs drawn uniformly at random, as in
/// RFC 6330 section 5.8. The RFC asks a compliant decoder to fail at most 1 in 100 times at K
/// (counting padding, that is K' symbols in all), 1 in 10,000 at K + 1 and 1 in 1,000,000 at
/// K + 2. The counts are written to the test output; the bounds asserted are deliberately loose,
/// since the trials are far too few to pin down rates that small. Seeds are fixed, so the counts
/// are the same on every run. Set MAILCAST_RAPTORQ_TRIALS to run more trials per K.
/// </summary>
public class DecodeFailureRateTests(ITestOutputHelper output)
{
    private static int Trials(int defaultTrials) =>
        int.TryParse(Environment.GetEnvironmentVariable("MAILCAST_RAPTORQ_TRIALS"), out int n) && n > 0 ? n : defaultTrials;

    [Theory]
    [InlineData(1, 2000)]
    [InlineData(10, 2000)]
    [InlineData(25, 2000)]
    [InlineData(64, 1000)]
    [InlineData(101, 500)]
    [InlineData(300, 200)]
    public void RandomEsis_FailureCountsAtKAndAbove(int k, int defaultTrials)
    {
        int trials = Trials(defaultTrials);
        const int t = 4;
        var rng = new Random(1000 + k);
        var failures = new int[3];
        for (int trial = 0; trial < trials; trial++)
        {
            var data = TestData.Random(trial, k * t);
            var encoder = new SourceBlockEncoder(k, t, data);
            var decoder = new SourceBlockDecoder(k, t);
            var esis = new HashSet<uint>();
            for (int extra = 0; extra <= 2; extra++)
            {
                while (esis.Count < k + extra)
                {
                    uint esi = (uint)rng.Next(1 << 24);
                    if (esis.Add(esi))
                    {
                        decoder.Add(esi, encoder.Encode(esi));
                    }
                }
                var result = decoder.TryDecode();
                if (result is null)
                {
                    failures[extra]++;
                }
                else
                {
                    Assert.Equal(data, result);
                }
            }
        }

        output.WriteLine($"K={k}, {trials} trials: failed at K {failures[0]}, at K+1 {failures[1]}, at K+2 {failures[2]}");
        Assert.True(failures[0] <= Math.Max(5, trials / 20), $"K: {failures[0]} of {trials}");
        Assert.True(failures[1] <= Math.Max(2, trials / 500), $"K+1: {failures[1]} of {trials}");
        Assert.True(failures[2] <= Math.Max(1, trials / 5000), $"K+2: {failures[2]} of {trials}");
    }
}
