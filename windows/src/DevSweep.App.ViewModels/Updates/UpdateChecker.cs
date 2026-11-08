using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevSweep.App.ViewModels.Updates;

/// <summary>A dotted version such as 0.8.2, with an optional prefix such as <c>windows-v</c> or <c>v</c>; a pre-release or build suffix is ignored.</summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    private AppVersion(IReadOnlyList<int> parts) => Parts = parts;

    public IReadOnlyList<int> Parts { get; }

    public static AppVersion? Parse(string? text, string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string s = text.Trim();
        if (prefix is { Length: > 0 })
        {
            if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            s = s[prefix.Length..];
        }
        else if (s.Length > 0 && s[0] is 'v' or 'V')
        {
            s = s[1..];
        }

        int end = 0;
        while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] == '.'))
        {
            end++;
        }

        if (end == 0)
        {
            return null;
        }

        string[] pieces = s[..end].Split('.');
        var parts = new List<int>(pieces.Length);
        foreach (string piece in pieces)
        {
            if (!int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                return null;
            }

            parts.Add(n);
        }

        return new AppVersion(parts);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int n = Math.Max(Parts.Count, other.Parts.Count);
        for (int i = 0; i < n; i++)
        {
            int a = i < Parts.Count ? Parts[i] : 0;
            int b = i < other.Parts.Count ? other.Parts[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return 0;
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as AppVersion);

    public override int GetHashCode()
    {
        // Trailing zeros do not count (1.2 equals 1.2.0).
        int last = Parts.Count;
        while (last > 1 && Parts[last - 1] == 0)
        {
            last--;
        }

        var hash = new HashCode();
        for (int i = 0; i < last; i++)
        {
            hash.Add(Parts[i]);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join('.', Parts);

    public static bool operator >(AppVersion left, AppVersion right) => left is not null && left.CompareTo(right) > 0;

    public static bool operator <(AppVersion left, AppVersion right) => left is not null && left.CompareTo(right) < 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left is not null && left.CompareTo(right) >= 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left is null || left.CompareTo(right) <= 0;

    public static bool operator ==(AppVersion? left, AppVersion? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(AppVersion? left, AppVersion? right) => !(left == right);
}

/// <summary>A published Windows release newer than the running app.</summary>
/// <param name="Version">"0.2.0".</param>
/// <param name="Tag">"windows-v0.2.0".</param>
/// <param name="PageUrl">The release page on github.com (always https, always github.com).</param>
/// <param name="Notes">The release notes (trimmed).</param>
/// <param name="PublishedAt">When it was published.</param>
public sealed record UpdateInfo(string Version, string Tag, Uri PageUrl, string Notes, DateTimeOffset? PublishedAt);

public enum UpdateOutcome
{
    UpToDate,
    Available,
    Failed,
}

/// <summary>The answer to one check. A failure is a message, never an exception.</summary>
public sealed record UpdateCheckResult(UpdateOutcome Outcome, UpdateInfo? Update, string Message)
{
    public static UpdateCheckResult UpToDate(string version) => new(UpdateOutcome.UpToDate, null, $"You're up to date (version {version}).");

    public static UpdateCheckResult Failed(string message) => new(UpdateOutcome.Failed, null, message);
}

/// <summary>Looks for a newer release of DevSweep for Windows.</summary>
public interface IUpdateChecker
{
    /// <summary>Asks once. Network problems, rate limits and odd answers come back as <see cref="UpdateOutcome.Failed"/>; only cancellation throws.</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>Checks nothing and contacts nothing (tests, and runs where the check is off).</summary>
public sealed class NoUpdateChecker : IUpdateChecker
{
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        => Task.FromResult(UpdateCheckResult.Failed("Checking for updates is not available here. Nothing was sent anywhere."));
}

/// <summary>
/// Asks GitHub for DevSweep's releases (one unauthenticated GET of the public release list) and picks the
/// newest published release tagged <c>windows-v*</c>, so the Mac app's releases in the same repository are
/// ignored. Nothing about the PC is sent: no identifier, no body, no query beyond the page size, and the
/// only request headers are Accept, the API version and a User-Agent naming DevSweep and its own version.
/// Offline, rate limits (403 or 429), server errors and unreadable answers are reported, never thrown.
/// </summary>
public sealed class GitHubUpdateChecker : IUpdateChecker, IDisposable
{
    public const string Repository = "karuneshpalekar/DevSweep";
    public const string TagPrefix = "windows-v";
    public const string ReleasesUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=30";

    private readonly HttpClient _http;
    private readonly string _current;

    /// <param name="currentVersion">The running app's version, such as "0.1.0".</param>
    /// <param name="handler">Replaces the network (tests answer with recorded responses); null uses the system's.</param>
    /// <param name="timeout">How long to wait for GitHub.</param>
    public GitHubUpdateChecker(string currentVersion, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _current = currentVersion ?? string.Empty;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevSweep", AppVersion.Parse(_current)?.ToString() ?? "unknown"));
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return UpdateCheckResult.Failed("GitHub is limiting requests right now. DevSweep will try again later.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed($"GitHub answered with status {(int)response.StatusCode}.");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Evaluate(json, _current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return UpdateCheckResult.Failed("Couldn't reach GitHub. Check your connection; DevSweep will try again later.");
        }
    }

    /// <summary>Compares GitHub's release list with the running version (public so tests feed it recorded answers).</summary>
    public static UpdateCheckResult Evaluate(string json, string currentVersion)
    {
        List<GitHubRelease>? releases;
        try
        {
            releases = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.ListGitHubRelease);
        }
        catch (JsonException)
        {
            releases = null;
        }

        if (releases is null)
        {
            return UpdateCheckResult.Failed("GitHub's answer wasn't what DevSweep expected.");
        }

        AppVersion? running = AppVersion.Parse(currentVersion);
        if (running is null)
        {
            return UpdateCheckResult.Failed("Couldn't read this version of DevSweep's number, so it can't be compared.");
        }

        GitHubRelease? best = null;
        AppVersion? bestVersion = null;
        foreach (GitHubRelease release in releases)
        {
            if (release.Draft || release.Prerelease || AppVersion.Parse(release.TagName, TagPrefix) is not { } version)
            {
                continue;
            }

            if (bestVersion is null || version > bestVersion)
            {
                best = release;
                bestVersion = version;
            }
        }

        if (best is null || bestVersion is null || bestVersion <= running)
        {
            return UpdateCheckResult.UpToDate(running.ToString());
        }

        Uri page = ReleasePage(best);
        string notes = (best.Body ?? string.Empty).Trim();
        if (notes.Length > 2000)
        {
            notes = notes[..2000] + "…";
        }

        var info = new UpdateInfo(bestVersion.ToString(), best.TagName!, page, notes, best.PublishedAt);
        return new UpdateCheckResult(UpdateOutcome.Available, info, $"Version {info.Version} is available.");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The release's own page when it is an https github.com address, else the page GitHub would give that tag.</summary>
    private static Uri ReleasePage(GitHubRelease release)
    {
        if (Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out Uri? url)
            && url.Scheme == Uri.UriSchemeHttps
            && string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(release.TagName ?? string.Empty)}");
    }
}

/// <summary>The few fields of GitHub's release JSON that are used.</summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }
}

[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
