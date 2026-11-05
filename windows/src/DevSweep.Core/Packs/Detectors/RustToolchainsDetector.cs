using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// <c>kind: rustToolchains</c>. rustup toolchains that are not the default, not a directory
/// override and not pinned by a <c>rust-toolchain</c> / <c>rust-toolchain.toml</c> in a project
/// folder, and only numbered ones (<c>1.75.0-...</c>; channels like nightly may be used with
/// <c>cargo +nightly</c>). When rustup's <c>settings.toml</c> cannot be read, nothing is offered.
/// <list type="bullet"><item><c>projectRoots</c>: where to look for pins (Settings' project folders replace them).</item></list>
/// </summary>
public sealed class RustToolchainsDetector : Detector<RustToolchainsDetector.Options>
{
    public override string Kind => "rustToolchains";

    /// <summary>The default toolchain and every override's toolchain in rustup's settings.toml.</summary>
    public static (string? Default, IReadOnlyList<string> Overrides) ReadSettings(string toml)
    {
        ArgumentNullException.ThrowIfNull(toml);
        string? defaultToolchain = null;
        var overrides = new List<string>();
        string section = string.Empty;
        foreach (string raw in toml.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']').Trim();
                continue;
            }

            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || line.StartsWith('#'))
            {
                continue;
            }

            string key = line[..eq].Trim().Trim('"');
            string value = line[(eq + 1)..].Trim().Trim('"');
            if (section.Length == 0 && key == "default_toolchain")
            {
                defaultToolchain = value;
            }
            else if (section == "overrides")
            {
                overrides.Add(value);
            }
        }

        return (defaultToolchain, overrides);
    }

    /// <summary>The channel a pin file names: <c>channel = "1.75.0"</c> in TOML, or the whole legacy file.</summary>
    public static string? PinnedChannel(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("channel", StringComparison.Ordinal) && line.IndexOf('=', StringComparison.Ordinal) is int eq and > 0)
            {
                return line[(eq + 1)..].Trim().Trim('"', '\'');
            }
        }

        string whole = text.Trim();
        return whole.Length > 0 && !whole.Contains('\n', StringComparison.Ordinal) && !whole.Contains('=', StringComparison.Ordinal) ? whole : null;
    }

    /// <summary>True when the toolchain folder (<c>1.75.0-x86_64-pc-windows-msvc</c>) is the channel (<c>1.75.0</c>, or the full name).</summary>
    public static bool Matches(string toolchain, string channel)
        => toolchain.Equals(channel, StringComparison.OrdinalIgnoreCase)
            || toolchain.StartsWith(channel + "-", StringComparison.OrdinalIgnoreCase);

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Options(options.Paths("projectRoots"));
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var env = run.Context.Locations.Environment;
        if (run.Context.Locations.Resolve("rustupHome").Path is not { } home)
        {
            return [];
        }

        List<ProbeEntry> toolchains = FileProbe.List(Path.Join(home, "toolchains")).Entries.Where(e => e.CanEnter).ToList();
        if (toolchains.Count < 2)
        {
            return [];
        }

        string settingsFile = Path.Join(home, "settings.toml");
        if (env.ReadAllText(settingsFile) is not { } settings)
        {
            run.Note($"{settingsFile} could not be read, so the default toolchain is unknown; nothing is offered");
            return [];
        }

        (string? defaultToolchain, IReadOnlyList<string> overrides) = ReadSettings(settings);
        if (defaultToolchain is null)
        {
            run.Note($"{settingsFile} names no default toolchain; nothing is offered");
            return [];
        }

        var kept = new List<(string Channel, string Why)> { (defaultToolchain, "the default") };
        kept.AddRange(overrides.Select(o => (o, "a directory override in settings.toml")));
        if (env.GetVariable("RUSTUP_TOOLCHAIN") is { } shell)
        {
            kept.Add((shell, "RUSTUP_TOOLCHAIN"));
        }

        IReadOnlyList<string> roots = ProjectFiles.Roots(run, options.ProjectRoots);
        foreach (string file in ProjectFiles.Find(run, roots, ["rust-toolchain", "rust-toolchain.toml"]))
        {
            if (env.ReadAllText(file) is { } text && PinnedChannel(text) is { } channel)
            {
                kept.Add((channel, $"pinned in {file}"));
            }
        }

        var candidates = new List<Candidate>();
        foreach (ProbeEntry toolchain in toolchains)
        {
            // Channels like "stable" match "stable-x86_64-pc-windows-msvc". Only numbered toolchains
            // (1.75.0-...) are ever offered: stable, beta and nightly may be used with cargo +nightly.
            if (!char.IsAsciiDigit(toolchain.Name[0]) || kept.Any(k => Matches(toolchain.Name, k.Channel)))
            {
                continue;
            }

            candidates.Add(new Candidate
            {
                Key = toolchain.Name,
                Paths = [toolchain.Path],
                Title = $"Rust toolchain {toolchain.Name}",
                Subtitle = toolchain.Path,
                Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["toolchain"] = toolchain.Name,
                    ["default"] = defaultToolchain,
                },
                Checks =
                [
                    Check.Passed($"Not the default ({defaultToolchain}, from {settingsFile})"),
                    Check.Passed(overrides.Count == 0 ? "No directory overrides" : $"Not a directory override ({Words.Join(overrides)})"),
                    Check.Passed($"No rust-toolchain file pins it in {(roots.Count == 0 ? "no project folders" : Words.Join(roots))}"),
                ],
            });
        }

        return candidates;
    }

    /// <summary>Bound options of a <c>rustToolchains</c> rule.</summary>
    public sealed record Options(IReadOnlyList<PathTemplate> ProjectRoots);
}
