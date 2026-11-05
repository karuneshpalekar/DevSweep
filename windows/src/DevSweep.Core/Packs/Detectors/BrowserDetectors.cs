using System.Text.Json;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>What a Chromium browser's <c>Local State</c> says about its profiles.</summary>
public sealed record ChromiumProfiles(IReadOnlyDictionary<string, ChromiumProfile> Profiles, string? LastUsed)
{
    public static ChromiumProfiles Empty { get; } = new(new Dictionary<string, ChromiumProfile>(), null);

    /// <summary>Reads <c>profile.info_cache</c> and <c>profile.last_used</c>; null when the file is not JSON.</summary>
    public static ChromiumProfiles? Parse(string localState)
    {
        ArgumentNullException.ThrowIfNull(localState);
        try
        {
            using var doc = JsonDocument.Parse(localState);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("profile", out JsonElement profile) || profile.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            string? lastUsed = profile.TryGetProperty("last_used", out JsonElement l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            var profiles = new Dictionary<string, ChromiumProfile>(StringComparer.OrdinalIgnoreCase);
            if (profile.TryGetProperty("info_cache", out JsonElement cache) && cache.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in cache.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
                {
                    static string? Text(JsonElement e, string n) => e.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
                    DateTime? active = p.Value.TryGetProperty("active_time", out JsonElement t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out double seconds)
                        ? DateTime.UnixEpoch.AddSeconds(seconds)
                        : null;
                    profiles[p.Name] = new ChromiumProfile(p.Name, Text(p.Value, "name") ?? p.Name, Text(p.Value, "user_name"), active);
                }
            }

            return new ChromiumProfiles(profiles, lastUsed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The profile's display name, as the browser shows it.</summary>
    public string NameOf(string folder) => folder switch
    {
        "Guest Profile" => "Guest",
        "System Profile" => "System",
        _ => Profiles.TryGetValue(folder, out ChromiumProfile? p) ? p.Name : folder,
    };
}

/// <summary>One profile in <c>Local State</c>.</summary>
public sealed record ChromiumProfile(string Folder, string Name, string? Account, DateTime? ActiveUtc);

/// <summary>
/// <c>kind: browserCaches</c> (the Mac <c>browserProfileCaches</c>, ported). A Chromium browser's
/// caches, one finding per profile so each can be left out: only the cache folders inside each
/// profile (<c>Cache</c>, <c>Code Cache</c>, <c>GPUCache</c>...), never the profile's data.
/// <list type="bullet">
/// <item><c>browser</c>: its name, for titles;</item>
/// <item><c>userData</c>: the browser's <c>User Data</c> folder;</item>
/// <item><c>caches</c>: cache folder names inside a profile (default: Cache, Code Cache, GPUCache, DawnGraphiteCache, DawnWebGPUCache).</item>
/// </list>
/// </summary>
public sealed class BrowserCachesDetector : Detector<BrowserCachesDetector.Options>
{
    private static readonly string[] DefaultCaches = ["Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache"];

    public override string Kind => "browserCaches";

    /// <summary>A folder Chromium uses as a profile: listed in Local State, <c>Default</c>, <c>Profile N</c>, Guest or System.</summary>
    public static bool IsProfileFolder(string name, ChromiumProfiles state)
        => state.Profiles.ContainsKey(name) || name is "Default" or "Guest Profile" or "System Profile"
            || (name.StartsWith("Profile ", StringComparison.Ordinal) && name.Length > 8 && name[8..].All(char.IsAsciiDigit));

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string browser = options.Text("browser", required: true) ?? "Browser";
        IReadOnlyList<PathTemplate> userData = options.Paths("userData", required: true);
        IReadOnlyList<string> caches = options.TextList("caches");
        return new Options(browser, userData, caches.Count == 0 ? DefaultCaches : caches);
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        var candidates = new List<Candidate>();
        foreach (PathTemplate template in options.UserData)
        {
            foreach (ProbeEntry userData in run.Expand(template).Matches.Where(m => m.CanEnter))
            {
                ChromiumProfiles state = run.Context.Locations.Environment.ReadAllText(Path.Join(userData.Path, "Local State")) is { } text
                    ? ChromiumProfiles.Parse(text) ?? ChromiumProfiles.Empty
                    : ChromiumProfiles.Empty;
                foreach (ProbeEntry profile in FileProbe.List(userData.Path).Entries.Where(e => e.CanEnter && IsProfileFolder(e.Name, state)))
                {
                    List<string> caches = options.Caches
                        .Select(c => FileProbe.Stat(Path.Join(profile.Path, c)))
                        .Where(e => e is { CanEnter: true })
                        .Select(e => e!.Value.Path)
                        .ToList();
                    if (caches.Count == 0)
                    {
                        continue;
                    }

                    string name = state.NameOf(profile.Name);
                    var bits = new List<string>();
                    if (!string.Equals(name, profile.Name, StringComparison.Ordinal))
                    {
                        bits.Add(profile.Name);
                    }

                    if (state.Profiles.TryGetValue(profile.Name, out ChromiumProfile? info) && info.Account is { } account)
                    {
                        bits.Add(account);
                    }

                    if (string.Equals(profile.Name, state.LastUsed, StringComparison.OrdinalIgnoreCase))
                    {
                        bits.Add($"the profile {options.Browser} opens with");
                    }

                    candidates.Add(new Candidate
                    {
                        Key = profile.Path,
                        Paths = caches,
                        Title = $"{options.Browser} cache ({name})",
                        Subtitle = bits.Count == 0 ? profile.Path : string.Join(", ", bits),
                        Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["browser"] = options.Browser,
                            ["profile"] = name,
                            ["userData"] = userData.Path,
                        },
                        Checks =
                        [
                            Check.Passed($"Only the cache folders ({Words.Join(caches.Select(Path.GetFileName).ToList()!)}) of the profile {name}"),
                            Check.Passed("Logins, passwords, bookmarks, history and extensions stay: they are elsewhere in the profile"),
                        ],
                    });
                }
            }
        }

        return candidates;
    }

    /// <summary>Bound options of a <c>browserCaches</c> rule.</summary>
    public sealed record Options(string Browser, IReadOnlyList<PathTemplate> UserData, IReadOnlyList<string> Caches);
}

/// <summary>
/// <c>kind: browserProfiles</c> (the Mac <c>chromeProfiles</c>, ported). Profiles of a Chromium
/// browser not used for <c>unusedDays</c> (from <c>active_time</c> in Local State), never the one
/// it opens with. Shown, never removed by DevSweep: the browser should delete them itself.
/// <list type="bullet">
/// <item><c>browser</c>, <c>userData</c>: as for <c>browserCaches</c>;</item>
/// <item><c>unusedDays</c>: default 180.</item>
/// </list>
/// </summary>
public sealed class BrowserProfilesDetector : Detector<BrowserProfilesDetector.Options>
{
    public override string Kind => "browserProfiles";

    protected override Options? BindOptions(OptionReader options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string browser = options.Text("browser", required: true) ?? "Browser";
        return new Options(browser, options.Paths("userData", required: true), options.Number("unusedDays", 1, 3650) ?? 180);
    }

    protected override IReadOnlyList<Candidate> Detect(DetectorRun run, Options options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        DateTime now = run.Context.Now.UtcDateTime;
        var candidates = new List<Candidate>();
        foreach (PathTemplate template in options.UserData)
        {
            foreach (ProbeEntry userData in run.Expand(template).Matches.Where(m => m.CanEnter))
            {
                if (run.Context.Locations.Environment.ReadAllText(Path.Join(userData.Path, "Local State")) is not { } text
                    || ChromiumProfiles.Parse(text) is not { } state)
                {
                    continue;
                }

                foreach (ChromiumProfile profile in state.Profiles.Values.OrderBy(p => p.Folder, StringComparer.Ordinal))
                {
                    string path = Path.Join(userData.Path, profile.Folder);
                    if (string.Equals(profile.Folder, state.LastUsed, StringComparison.OrdinalIgnoreCase)
                        || profile.ActiveUtc is not { } active || active >= now.AddDays(-options.UnusedDays)
                        || FileProbe.Stat(path) is not { CanEnter: true })
                    {
                        continue;
                    }

                    string ago = LargeOldFilesDetector.Ago(active, now);
                    var checks = new List<Check>
                    {
                        Check.Passed($"Last used {ago} ({active:yyyy-MM-dd}, from {options.Browser}'s Local State)"),
                        Check.Passed($"It isn't the profile {options.Browser} opens with"),
                        profile.Account is { } account
                            ? Check.Passed($"Signed in as {account}, so synced data is also in that account")
                            : Check.Warning("Not signed in: its bookmarks and passwords exist only on this PC"),
                    };
                    candidates.Add(new Candidate
                    {
                        Key = path,
                        Paths = [path],
                        Title = $"{options.Browser} profile “{profile.Name}”",
                        Subtitle = profile.Account is null ? $"last used {ago}" : $"{profile.Account}, last used {ago}",
                        Facts = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["browser"] = options.Browser,
                            ["profile"] = profile.Name,
                            ["ago"] = ago,
                        },
                        Checks = checks,
                    });
                }
            }
        }

        return candidates;
    }

    /// <summary>Bound options of a <c>browserProfiles</c> rule.</summary>
    public sealed record Options(string Browser, IReadOnlyList<PathTemplate> UserData, int UnusedDays);
}
