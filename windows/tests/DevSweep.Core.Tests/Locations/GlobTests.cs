using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using Xunit;

namespace DevSweep.Core.Tests.Locations;

public sealed class GlobTests
{
    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("*", "", true)]
    [InlineData("IntelliJ*", "intellijidea2024.1", true)]
    [InlineData("*.log", "app.LOG", true)]
    [InlineData("*.log", "app.log.old", false)]
    [InlineData("*.*", "nodot", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("[abc]x", "Bx", true)]
    [InlineData("[a-c]x", "dx", false)]
    [InlineData("[!a-c]x", "dx", true)]
    [InlineData("[!a-c]x", "ax", false)]
    [InlineData("[]]x", "]x", true)]
    [InlineData("*cache*", "npm-Cache-v2", true)]
    [InlineData("a*b*c", "aXbYbZc", true)]
    [InlineData("a*b*c", "aXbYbZ", false)]
    [InlineData("PROGRA~1", "Program Files", false)]
    public void Matches_within_one_segment_case_insensitively(string pattern, string name, bool expected)
        => Assert.Equal(expected, Glob.IsMatch(pattern, name));

    [Fact]
    public void Templates_split_on_both_separators_and_keep_wildcards()
    {
        Assert.True(PathTemplate.TryParse(@"{LocalAppData}\JetBrains/*\caches", null, out PathTemplate? template, out _));
        Assert.Equal("LocalAppData", template!.Root);
        Assert.Equal(["JetBrains", "*", "caches"], template.Segments);
        Assert.True(template.HasWildcards);

        Assert.True(PathTemplate.TryParse("{Temp}", null, out PathTemplate? bare, out _));
        Assert.Empty(bare!.Segments);
        Assert.False(PathTemplate.TryParse("{Temp}x", null, out _, out string error));
        Assert.Contains("put a \\ after the root", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2024.10", "2024.9", 1)]
    [InlineData("2024.1", "2023.3", 1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("8.0", "8.0.0", -1)]
    [InlineData("30.0.3", "30.0.3", 0)]
    [InlineData("v2", "v10", -1)]
    public void Versions_compare_number_by_number(string a, string b, int sign)
        => Assert.Equal(sign, Math.Sign(VersionComparer.Instance.Compare(a, b)));
}
