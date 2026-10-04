namespace Mailcast.RaptorQ.Tests;

public class BlockParametersTests
{
    private static bool IsPrime(int n)
    {
        if (n < 2)
        {
            return false;
        }
        for (int d = 2; d * d <= n; d++)
        {
            if (n % d == 0)
            {
                return false;
            }
        }
        return true;
    }

    [Fact]
    public void Table2_HasTheRfcShape()
    {
        var table = RfcTables.SystematicIndices;
        Assert.Equal(477 * 5, table.Length);
        int previous = 0;
        for (int row = 0; row < 477; row++)
        {
            int kPrime = table[row * 5];
            Assert.True(kPrime > previous);
            Assert.True(IsPrime(table[(row * 5) + 2]), $"S({kPrime}) is not prime");
            Assert.True(IsPrime(table[(row * 5) + 4]), $"W({kPrime}) is not prime");
            previous = kPrime;
        }
        Assert.Equal(10, table[0]);
        Assert.Equal(BlockParameters.MaxSourceSymbols, previous);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(10, 10)]
    [InlineData(11, 12)]
    [InlineData(25, 26)]
    [InlineData(56403, 56403)]
    public void ExtendedSize_IsTheSmallestTableEntryAtLeastK(int k, int kPrime)
    {
        Assert.Equal(kPrime, BlockParameters.ExtendedSize(k));
        Assert.Equal(kPrime, BlockParameters.ForSourceSymbols(k).KPrime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(56404)]
    public void ForSourceSymbols_RejectsOutOfRange(int k)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockParameters.ForSourceSymbols(k));
    }

    [Fact]
    public void DerivedParameters_HoldTogether()
    {
        foreach (int k in new[] { 1, 10, 100, 1000, 10000, 56403 })
        {
            var p = BlockParameters.ForSourceSymbols(k);
            Assert.Equal(p.KPrime + p.S + p.H, p.L);
            Assert.Equal(p.L, p.W + p.P);
            Assert.True(IsPrime(p.P1) && p.P1 >= p.P);
            Assert.Equal(p.P - p.H, p.U);
            Assert.Equal(p.W - p.S, p.B);
        }
    }

    [Fact]
    public void Tuple_StaysInTheRangesOfSection5_3_5_4()
    {
        var p = BlockParameters.ForSourceSymbols(100);
        for (uint x = 0; x < 5000; x++)
        {
            var t = p.Tuple(x);
            Assert.InRange(t.D, 1, 30);
            Assert.InRange(t.A, 1, p.W - 1);
            Assert.InRange(t.B, 0, p.W - 1);
            Assert.InRange(t.D1, 2, 3);
            Assert.InRange(t.A1, 1, p.P1 - 1);
            Assert.InRange(t.B1, 0, p.P1 - 1);
        }
    }
}
