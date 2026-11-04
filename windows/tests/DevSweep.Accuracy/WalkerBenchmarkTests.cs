using System.Diagnostics;
using System.Globalization;
using DevSweep.Core.Platform;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>
/// M0 walker against walker v2 on 20,000 files (200 folders of 100), each
/// measured five times after a warm-up; the median is logged. All modes must
/// give identical counts. The numbers go in the README.
/// </summary>
public sealed class WalkerBenchmarkTests(ITestOutputHelper output) : IDisposable
{
    private const int Folders = 200;
    private const int FilesPerFolder = 100;
    private const int Runs = 5;
    private readonly TempFixture _fixture = new("bench");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Walker_v2_against_m0_on_20000_files()
    {
        var build = Stopwatch.StartNew();
        string root = _fixture.CreateDirectory("tree");
        byte[] content = new byte[1500];
        for (int d = 0; d < Folders; d++)
        {
            string folder = Directory.CreateDirectory(Path.Combine(root, $"pkg{d:000}", "lib")).FullName;
            for (int f = 0; f < FilesPerFolder; f++)
            {
                content[0] = (byte)f;
                File.WriteAllBytes(Path.Combine(folder, $"module{f:000}.js"), content.AsSpan(0, 200 + (f * 37 % 1300)).ToArray());
            }
        }

        output.WriteLine($"fixture: {Folders * FilesPerFolder:N0} files in {Folders * 2 + 1} folders, built in {build.Elapsed.TotalSeconds:0.0}s ({Elevation.Describe()})");

        (string Name, WalkOptions Options)[] modes =
        [
            ("M0 (FindFirstFileExW + open each file)", new WalkOptions { Listing = ListingMethod.FindFirstFile, AlternateStreams = false }),
            ("v2 default (FileIdExtd + streams)", WalkOptions.Default),
            ("v2 no streams (FileIdExtd + by name)", new WalkOptions { AlternateStreams = false }),
            ("v2 fallback (FindFirstFileExW + streams)", new WalkOptions { Listing = ListingMethod.FindFirstFile }),
        ];

        WalkResult? reference = null;
        double m0Median = 0;
        foreach ((string name, WalkOptions options) in modes)
        {
            Walk.Measure(root, options); // warm-up
            var times = new List<double>();
            WalkResult last = null!;
            for (int run = 0; run < Runs; run++)
            {
                last = Walk.Measure(root, options);
                times.Add(last.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            double median = times[Runs / 2];
            m0Median = m0Median == 0 ? median : m0Median;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"BENCHMARK {name,-44} median {median,8:0.0} ms  min {times[0],8:0.0} ms  max {times[^1],8:0.0} ms  ({median / m0Median:0.00}x M0)  files={last.Files} bytes={last.Bytes} listing={last.Listing} stale={last.StaleDirectoryEntries}"));

            reference ??= last;
            Assert.True(last.Complete);
            Assert.Equal(Folders * FilesPerFolder, last.Files);
            Assert.Equal(reference.Bytes, last.Bytes);
            Assert.Equal(reference.LogicalBytes, last.LogicalBytes);
            Assert.Equal(reference.Directories, last.Directories);
        }
    }
}
