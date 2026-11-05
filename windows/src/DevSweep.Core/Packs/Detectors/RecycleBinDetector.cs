using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Packs.Tools;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: recycleBin</c>. What the Recycle Bin holds on each fixed drive (<c>SHQueryRecycleBin</c>),
/// for information: emptying it is the user's choice (and it holds what DevSweep recycled, which
/// History can still restore until then). No options.
/// </summary>
public sealed class RecycleBinDetector(IMachineQueries machine) : Detector<RecycleBinDetector.Options>
{
    public override string Kind => "recycleBin";

    protected override Options? BindOptions(OptionReader options) => new();

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        var candidates = new List<Candidate>();
        foreach (string drive in machine.FixedDriveRoots())
        {
            if (machine.RecycleBin(drive) is not RecycleBinUsage usage || usage.Items == 0)
            {
                continue;
            }

            string letter = drive.TrimEnd('\\');
            candidates.Add(new Candidate
            {
                Key = letter,
                Size = usage.Bytes,
                Title = $"Recycle Bin on {letter}",
                Subtitle = string.Create(CultureInfo.InvariantCulture, $"{usage.Items:N0} items"),
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["drive"] = letter,
                    ["items"] = usage.Items.ToString("N0", CultureInfo.InvariantCulture),
                },
                Checks = [Check.Passed(string.Create(CultureInfo.InvariantCulture, $"Windows reports {usage.Items:N0} items, {SizeText.Format(usage.Bytes)}, in the Recycle Bin on {letter} (SHQueryRecycleBin)"))],
            });
        }

        return candidates;
    }

    /// <summary>Bound options of a <c>recycleBin</c> rule (none).</summary>
    public sealed record Options;
}
