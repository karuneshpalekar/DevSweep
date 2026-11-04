using DevSweep.Core.Platform;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>Runs the walker with the test's cancellation token.</summary>
internal static class Walk
{
    public static WalkResult Measure(string path, WalkOptions? options = null)
        => DirectoryWalker.Measure(path, options, TestContext.Current.CancellationToken);
}
