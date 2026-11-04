using DevSweep.Core.Cleaning;
using DevSweep.Core.Inventory;
using DevSweep.Core.Locations;
using DevSweep.Core.Scanning;

namespace DevSweep.Cli.Output;

/// <summary><c>devsweep scan --json</c>: the findings, the scan report, and every named location as resolved.</summary>
internal sealed record ScanJson(IReadOnlyList<Finding> Findings, ScanReport Report, IReadOnlyList<ResolvedLocation> Locations);

/// <summary><c>devsweep inventory --json</c>.</summary>
internal sealed record InventoryJson(IReadOnlyList<SourceReport> Sources, IReadOnlyList<InventorySource> DisabledSources, int Records, IReadOnlyList<InstalledApp> Apps);
