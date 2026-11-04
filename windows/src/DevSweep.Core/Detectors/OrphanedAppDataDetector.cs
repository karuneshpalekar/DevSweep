using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Inventory;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Detectors;

/// <summary>
/// <c>kind: orphanedAppData</c>. Data folders of apps that are no longer installed.
/// Conservative by design: only apps listed in <c>knownApps</c> (each with the
/// folders it leaves and how to recognise it), and only when every inventory
/// source was read completely and none lists the app. If any of the app's folders
/// holds an installed program (an install location or a program path inside it),
/// none of them is offered. The finding lists every source that was checked.
/// <code>
/// knownApps:
///   - name: Postman
///     names: ["Postman"]          # display names (default: name)
///     publishers: [...]           # vendor-wide folders
///     productCodes, packageFamilies, packageIds, exes, folders: [...]
///     paths: ["{RoamingAppData}\Postman"]   # under AppData, ProgramData or the profile
///     note: "May contain collections you never synced."
/// </code>
/// </summary>
public sealed class OrphanedAppDataDetector : Detector<OrphanedAppDataDetector.Options>
{
    private static readonly HashSet<string> AllowedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "LocalAppData", "RoamingAppData", "LocalLow", "ProgramData", "Profile",
    };

    public override string Kind => "orphanedAppData";

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var apps = new List<KnownApp>();
        foreach (OptionReader app in options.Mappings("knownApps", required: true))
        {
            string? name = app.Text("name", required: true);
            IReadOnlyList<string> names = app.TextList("names");
            var hint = new AppHint
            {
                Name = name ?? string.Empty,
                Names = names.Count > 0 ? names : name is null ? [] : [name],
                Publishers = app.TextList("publishers"),
                ProductCodes = app.TextList("productCodes"),
                PackageFamilies = app.TextList("packageFamilies"),
                PackageIds = app.TextList("packageIds"),
                Exes = app.TextList("exes"),
                Folders = app.TextList("folders"),
            };
            IReadOnlyList<PathTemplate> paths = app.Paths("paths", required: true);
            foreach (PathTemplate path in paths.Where(p => !AllowedRoots.Contains(p.Root) || p.Segments.Count == 0))
            {
                app.Errors.Add(app.Node.Get("paths"), $"{path.Text}: leftover paths must be inside AppData, ProgramData or the profile, below the root itself");
            }

            string? note = app.Text("note");
            app.Finish();
            if (name is not null)
            {
                apps.Add(new KnownApp(hint, paths, note));
            }
        }

        return new Options(apps);
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var candidates = new List<Candidate>();
        if (!run.Context.HasInventory)
        {
            run.Note("No installed-apps inventory in this scan (module:inventory is off), so no leftovers are looked for");
            return candidates;
        }

        foreach (KnownApp app in options.KnownApps)
        {
            run.ThrowIfCancelled();
            List<ProbeEntry> places = app.Paths.SelectMany(p => run.Expand(p).Matches).DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
            if (places.Count == 0)
            {
                continue;
            }

            InstallMatch match = run.Context.Inventory.IsInstalled(app.Hint);
            if (match.State == InstallState.Unknown)
            {
                run.Note($"{app.Hint.Name}: leftovers not checked because {match.UnknownBecause}");
                continue;
            }

            if (match.IsInstalled)
            {
                continue;
            }

            if (places.Select(p => (Place: p, Inside: run.Context.Inventory.RecordsInside(p.Path))).FirstOrDefault(x => x.Inside.Count > 0) is { Inside.Count: > 0 } holder)
            {
                AppRecord installed = holder.Inside[0];
                run.Note($"{app.Hint.Name}: left alone because {holder.Place.Path} holds {installed.Name} ({InventorySources.Title(installed.Source)}), which is installed");
                continue;
            }

            candidates.Add(ToCandidate(app, places, match));
        }

        return candidates;
    }

    private static Candidate ToCandidate(KnownApp app, List<ProbeEntry> places, InstallMatch match)
    {
        string name = app.Hint.Name;
        string count = places.Count.ToString(CultureInfo.InvariantCulture);
        string placesText = $"{count} place{(places.Count == 1 ? "" : "s")}";
        var allChecks = new List<Check> { Check.Passed($"{name} is not installed: every source below was read and none lists it") };
        allChecks.AddRange(match.Checked.Select(source => Check.Passed(
            string.Create(CultureInfo.InvariantCulture, $"Not in {source.Title} ({source.Count} app{(source.Count == 1 ? "" : "s")} listed)"))));
        if (app.Note is { } note)
        {
            allChecks.Add(Check.Warning(note));
        }

        return new Candidate
        {
            Key = name,
            Paths = places.Select(p => p.Path).ToList(),
            Title = name,
            Subtitle = $"{placesText} · app not installed",
            Facts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app"] = name,
                ["places"] = placesText,
                ["sources"] = Words.Join(match.Checked.Select(s => s.Title).ToList()),
                ["sourceCount"] = match.Checked.Count.ToString(CultureInfo.InvariantCulture),
            },
            Checks = allChecks,
        };
    }

    /// <summary>An app this rule knows: how to recognise it, what it leaves behind, and a warning.</summary>
    public sealed record KnownApp(AppHint Hint, IReadOnlyList<PathTemplate> Paths, string? Note);

    /// <summary>Bound options of an <c>orphanedAppData</c> rule.</summary>
    public sealed record Options(IReadOnlyList<KnownApp> KnownApps);
}
