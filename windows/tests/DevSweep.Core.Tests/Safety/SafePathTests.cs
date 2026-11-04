using DevSweep.Core.Safety;
using Xunit;

namespace DevSweep.Core.Tests.Safety;

public sealed class SafePathTests
{
    [Theory]
    [InlineData(@"C:\Users\dev\x", @"C:\Users\dev\x")]
    [InlineData(@"c:\Users\dev\x\", @"C:\Users\dev\x")]
    [InlineData(@"C:/Users/dev/x", @"C:\Users\dev\x")]
    [InlineData(@"\\?\C:\Users\dev\x", @"C:\Users\dev\x")]
    [InlineData(@"\\?\C:\Users\dev\x\\", @"C:\Users\dev\x")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\server\share\a", @"\\server\share\a")]
    [InlineData(@"\\?\UNC\server\share\a\", @"\\server\share\a")]
    [InlineData(@"\\server\share", @"\\server\share")]
    public void Canonical_forms(string input, string expected)
    {
        Assert.True(SafePath.TryCanonicalize(input, out string? canonical, out string rejection), rejection);
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Users\dev")]
    [InlineData(@"C:Users\dev")]
    [InlineData(@"\Users\dev")]
    [InlineData(@"C:\Users\..\Windows")]
    [InlineData(@"C:\Users\dev\..")]
    [InlineData(@"C:\Users\.\dev")]
    [InlineData(@"C:\Users\\dev")]
    [InlineData(@"C:\Users\dev\Documents.")]
    [InlineData(@"C:\Users\dev\Documents ")]
    [InlineData(@"C:\Users\dev\file.txt:stream")]
    [InlineData(@"C:\Users\dev\*")]
    [InlineData(@"C:\Users\dev\a?b")]
    [InlineData(@"C:\Users\dev\a<b")]
    [InlineData(@"\\.\C:\Users")]
    [InlineData(@"\??\C:\Users")]
    [InlineData(@"\\?\Volume{3f1c2b4a-0000-0000-0000-100000000000}\x")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x")]
    [InlineData(@"\\server")]
    [InlineData("C:\\a\u0001b")]
    public void Ambiguous_or_unsafe_forms_are_refused(string input)
    {
        Assert.False(SafePath.TryCanonicalize(input, out string? canonical, out string rejection));
        Assert.Null(canonical);
        Assert.NotEmpty(rejection);
    }

    [Fact]
    public void A_segment_over_255_characters_is_refused()
        => Assert.False(SafePath.TryCanonicalize(@"C:\" + new string('a', 256), out _, out _));

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"C:\a\b", @"c:\A", true)]
    [InlineData(@"C:\ab", @"C:\a", false)]
    [InlineData(@"C:\a", @"C:\a", false)]
    [InlineData(@"C:\a", @"C:\", true)]
    [InlineData(@"\\s\h\a", @"\\s\h", true)]
    [InlineData(@"D:\a\b", @"C:\a", false)]
    public void Strictly_under(string path, string ancestor, bool expected)
        => Assert.Equal(expected, SafePath.IsStrictlyUnder(path, ancestor));

    [Fact]
    public void Relative_segments()
        => Assert.Equal(["a", "b"], SafePath.RelativeSegments(@"C:\p\a\b", @"C:\p"));
}
