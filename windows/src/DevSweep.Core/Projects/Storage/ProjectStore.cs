using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevSweep.Core.Platform;
using DevSweep.Core.Storage;

namespace DevSweep.Core.Projects.Storage;

/// <summary>Source-generated JSON for what Projects stores: no reflection, AOT and trim safe.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProjectsState))]
public sealed partial class ProjectsJsonContext : JsonSerializerContext;

/// <summary>
/// Keeps <see cref="ProjectsState"/> in <c>%LOCALAPPDATA%\DevSweep\projects.json</c>: read once,
/// every change written with <see cref="AtomicFile"/> (M1: temp file written through, then
/// <c>ReplaceFileW</c>), an unreadable file set aside rather than overwritten (like History).
/// Safe to use from several threads.
/// </summary>
public sealed class ProjectStore
{
    /// <summary>Activity lines kept (the Mac app keeps 500).</summary>
    public const int MaxActivity = 500;

    private readonly Lock _lock = new();
    private ProjectsState _state = new();

    public ProjectStore(string? path = null)
    {
        FilePath = path ?? DefaultPath;
        Load();
    }

    /// <summary><c>%LOCALAPPDATA%\DevSweep\projects.json</c>.</summary>
    public static string DefaultPath
        => Path.Join(OperatingSystem.IsWindows() ? KnownFolders.LocalAppData : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevSweep", "projects.json");

    public string FilePath { get; }

    /// <summary>Set when the file could not be read; it was kept under this name.</summary>
    public string? SetAsidePath { get; private set; }

    public ProjectsState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath))
            {
                _state = new ProjectsState();
                return;
            }

            try
            {
                _state = (JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), ProjectsJsonContext.Default.ProjectsState) ?? new ProjectsState()).Normalised();
            }
            catch (JsonException)
            {
                SetAsidePath = string.Create(CultureInfo.InvariantCulture, $"{FilePath}.unreadable-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
                File.Move(FilePath, SetAsidePath);
                _state = new ProjectsState();
            }
        }
    }

    /// <summary>Applies a change and saves it.</summary>
    public ProjectsState Update(Func<ProjectsState, ProjectsState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            _state = change(_state);
            AtomicFile.Write(FilePath, JsonSerializer.SerializeToUtf8Bytes(_state, ProjectsJsonContext.Default.ProjectsState));
            return _state;
        }
    }

    /// <summary>Adds a line to the Activity trail (newest first, the last <see cref="MaxActivity"/> kept).</summary>
    public ActivityEntry Log(ActivityKind kind, string subject, string detail = "")
    {
        var entry = new ActivityEntry { Kind = kind, Subject = subject, Detail = detail };
        Update(s => s with { Activity = [entry, .. s.Activity.Take(MaxActivity - 1)] });
        return entry;
    }

    /// <summary>The commit identity saved for an account (case-insensitive), or null.</summary>
    public GitIdentity? Identity(string? account)
        => account is null ? null : State.Identities.FirstOrDefault(p => p.Key.Equals(account, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Saves (or, when blank, clears) an account's identity and records it.</summary>
    public void SaveIdentity(string account, GitIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Update(s =>
        {
            var identities = s.Identities.Where(p => !p.Key.Equals(account, StringComparison.OrdinalIgnoreCase)).ToDictionary(StringComparer.OrdinalIgnoreCase);
            if (!identity.IsBlank)
            {
                identities[account] = identity;
            }

            return s with { Identities = identities };
        });
        Log(ActivityKind.Identity, account, identity.IsBlank ? "Cleared the commit identity" : $"Commits are now made as {identity}");
    }

    /// <summary>Adds or updates a remembered repository (matched case-insensitively).</summary>
    public void Remember(string nameWithOwner, Func<KnownRepo?, KnownRepo> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Update(s =>
        {
            int index = s.Known.FindIndex(k => k.NameWithOwner.Equals(nameWithOwner, StringComparison.OrdinalIgnoreCase));
            var known = s.Known.ToList();
            if (index >= 0)
            {
                known[index] = change(known[index]);
            }
            else
            {
                known.Add(change(null));
            }

            return s with { Known = known };
        });
    }

    public KnownRepo? Known(string nameWithOwner)
        => State.Known.Find(k => k.NameWithOwner.Equals(nameWithOwner, StringComparison.OrdinalIgnoreCase));
}
