using DevSweep.Core.Platform;
using Xunit;

namespace DevSweep.Core.Tests.Platform;

public sealed class LongPathTests
{
    [Theory]
    [InlineData(@"C:\Users\dev\project", @"\\?\C:\Users\dev\project")]
    [InlineData(@"C:\Users\dev\project\", @"\\?\C:\Users\dev\project")]
    [InlineData(@"C:/Users/dev/project", @"\\?\C:\Users\dev\project")]
    [InlineData(@"C:\", @"\\?\C:\")]
    [InlineData(@"\\server\share\folder", @"\\?\UNC\server\share\folder")]
    [InlineData(@"\\?\C:\Users\dev", @"\\?\C:\Users\dev")]
    public void ToExtended_adds_the_right_prefix(string input, string expected)
        => Assert.Equal(expected, LongPath.ToExtended(input));

    [Theory]
    [InlineData(@"\\?\C:\a\b", @"C:\a\b")]
    [InlineData(@"\\?\UNC\server\share\a", @"\\server\share\a")]
    [InlineData(@"C:\a", @"C:\a")]
    public void StripPrefix_returns_the_display_path(string input, string expected)
        => Assert.Equal(expected, LongPath.StripPrefix(input));

    [Fact]
    public void Paths_past_260_characters_round_trip()
    {
        string deep = @"C:\" + string.Join('\\', Enumerable.Repeat("a-folder-name-of-twenty", 20));
        Assert.True(deep.Length > 260);
        Assert.Equal(deep, LongPath.StripPrefix(LongPath.ToExtended(deep)));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"\\server\share", true)]
    public void IsRoot(string path, bool expected) => Assert.Equal(expected, LongPath.IsRoot(path));

    [Theory]
    [InlineData(@"\\?\C:\", "x", @"\\?\C:\x")]
    [InlineData(@"\\?\C:\a", "x", @"\\?\C:\a\x")]
    public void Join_uses_one_separator(string directory, string name, string expected)
        => Assert.Equal(expected, LongPath.Join(directory, name));
}
