using System.Globalization;
using System.Text.Json.Serialization;
using DevSweep.Core.Modules;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Actions;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;
using DevSweep.Core.Scanning;

namespace DevSweep.Cli.Commands.Projects;

/// <summary>Shared pieces of the projects commands.</summary>
internal static class ProjectsCli
{
    /// <summary>Projects (or the tab asked for) is turned off; the same code as Health uses.</summary>
    public const int TurnedOff = 3;

    /// <summary>Something would be lost (or could not be checked): <c>projects check</c> and <c>remove</c>.</summary>
    public const int NotSafe = 1;

    public const int Failed = 2;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>The Projects service, or null after printing how to turn it on.</summary>
    public static ProjectsService? Open(Func<ProjectsFeatures, bool>? tab = null, string? flag = null)
    {
        DevSweepEngine engine = EngineFactory.Create();
        ProjectsService? service = ProjectsService.Create(engine, Http);
        if (service is null)
        {
            Console.Error.WriteLine("devsweep: Projects is turned off (module:projects). Turn it on with DEVSWEEP_FEATURES=+module:projects, or add \"module:projects\" to \"enabled\" in %LOCALAPPDATA%\\DevSweep\\features.json.");
            return null;
        }

        if (tab is not null && !tab(service.Features))
        {
            Console.Error.WriteLine($"devsweep: this part of Projects is turned off ({flag}).");
            return null;
        }

        return service;
    }

    public static string When(DateTimeOffset? date)
        => date is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "never";

    public static void PrintSafety(RemovalSafety s)
    {
        Console.Out.WriteLine($"{s.Path}: {s.State} - {s.Headline}");
        foreach (DevSweep.Core.Cleaning.Check check in s.Checks)
        {
            Console.Out.WriteLine($"  {(check.Status == DevSweep.Core.Cleaning.CheckStatus.Passed ? "ok  " : "warn")} {check.Text}");
        }

        foreach (string blocker in s.Blockers)
        {
            Console.Out.WriteLine($"  would lose: {blocker}");
        }
    }

    public static string Size(long bytes) => SizeText.Format(bytes);
}

/// <summary>What <c>projects remove --json</c> prints.</summary>
internal sealed record RemoveJson(bool Succeeded, string Message, RemovalSafety? Safety, Guid? HistoryId, long Freed, IReadOnlyList<string> WentTo);

/// <summary>Source-generated JSON for the projects commands (AOT and trim safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProjectsSnapshot))]
[JsonSerializable(typeof(RemovalSafety))]
[JsonSerializable(typeof(IdleCleanupResult))]
[JsonSerializable(typeof(GitHubAccounts))]
[JsonSerializable(typeof(List<ActivityEntry>))]
[JsonSerializable(typeof(DownloadResult))]
[JsonSerializable(typeof(DownloadPlan))]
[JsonSerializable(typeof(RemoveJson))]
[JsonSerializable(typeof(PublishPreview))]
[JsonSerializable(typeof(TerminalLaunch))]
internal sealed partial class ProjectsCliJsonContext : JsonSerializerContext;
