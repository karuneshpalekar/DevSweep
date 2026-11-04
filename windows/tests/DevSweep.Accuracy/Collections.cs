using Xunit;

namespace DevSweep.Accuracy;

/// <summary>
/// Tests that compare a volume's free space before and after, or time the
/// walker, run on their own after everything else (other tests write files to
/// the same volume and would move the numbers).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AloneOnTheVolume
{
    public const string Name = "Alone on the volume";
}
