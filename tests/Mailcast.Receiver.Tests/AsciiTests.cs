namespace Mailcast.Receiver.Tests;

public class AsciiTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("spec \u00A73.3: at most \u22646", "spec section 3.3: at most <=6")]
    [InlineData("caf\u00E9 \u2014 bar", "caf? - bar")]
    [InlineData("tab\tand\r\nline", "tab and??line")]
    public void Clean_LeavesPrintableAsciiOnly(string text, string expected)
    {
        Assert.Equal(expected, Ascii.Clean(text));
    }
}
