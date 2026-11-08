using System.Net;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Updates;
using Xunit;

namespace DevSweep.App.Tests;

/// <summary>Responses recorded from GitHub's releases API (trimmed to the fields that matter). No test touches the network.</summary>
internal static class ReleaseFixtures
{
    /// <summary>The Mac app's release, a Windows pre-release, the real Windows releases and a draft, in GitHub's order (newest first).</summary>
    public const string Mixed = """
        [
          {"url":"https://api.github.com/repos/karuneshpalekar/DevSweep/releases/1004","html_url":"https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v0.9.9","id":1004,"tag_name":"windows-v0.9.9","draft":true,"prerelease":false,"published_at":null,"body":"draft, never published"},
          {"url":"https://api.github.com/repos/karuneshpalekar/DevSweep/releases/1003","html_url":"https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v0.3.0-beta.1","id":1003,"tag_name":"windows-v0.3.0-beta.1","draft":false,"prerelease":true,"published_at":"2026-11-06T08:00:00Z","body":"beta"},
          {"url":"https://api.github.com/repos/karuneshpalekar/DevSweep/releases/1002","html_url":"https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v0.2.0","id":1002,"tag_name":"windows-v0.2.0","name":"DevSweep for Windows 0.2.0","draft":false,"prerelease":false,"published_at":"2026-11-05T10:00:00Z","body":"Tray icon, scheduled scans, alerts.","assets":[{"name":"DevSweep-win-x64.zip","browser_download_url":"https://github.com/karuneshpalekar/DevSweep/releases/download/windows-v0.2.0/DevSweep-win-x64.zip"}]},
          {"url":"https://api.github.com/repos/karuneshpalekar/DevSweep/releases/1001","html_url":"https://github.com/karuneshpalekar/DevSweep/releases/tag/v0.9.6","id":1001,"tag_name":"v0.9.6","name":"DevSweep 0.9.6","draft":false,"prerelease":false,"published_at":"2026-10-20T10:00:00Z","body":"The Mac app.","assets":[{"name":"DevSweep.dmg","browser_download_url":"https://github.com/karuneshpalekar/DevSweep/releases/download/v0.9.6/DevSweep.dmg"}]},
          {"url":"https://api.github.com/repos/karuneshpalekar/DevSweep/releases/1000","html_url":"https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v0.1.5","id":1000,"tag_name":"windows-v0.1.5","draft":false,"prerelease":false,"published_at":"2026-10-01T10:00:00Z","body":"older"}
        ]
        """;

    public const string OnlyMac = """
        [{"html_url":"https://github.com/karuneshpalekar/DevSweep/releases/tag/v0.9.6","tag_name":"v0.9.6","draft":false,"prerelease":false,"published_at":"2026-10-20T10:00:00Z","body":"The Mac app."}]
        """;

    public const string TenIsNewerThanNine = """
        [{"html_url":"https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v0.10.0","tag_name":"windows-v0.10.0","draft":false,"prerelease":false,"published_at":"2026-12-01T10:00:00Z","body":""},
         {"html_url":"https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v0.9.0","tag_name":"windows-v0.9.0","draft":false,"prerelease":false,"published_at":"2026-11-01T10:00:00Z","body":""}]
        """;

    /// <summary>A release whose page address points somewhere that is not github.com over https.</summary>
    public const string OddPage = """
        [{"html_url":"http://downloads.example.net/windows-v0.2.0","tag_name":"windows-v0.2.0","draft":false,"prerelease":false,"published_at":"2026-11-05T10:00:00Z","body":"x"}]
        """;

    public const string RateLimited = """
        {"message":"API rate limit exceeded for 203.0.113.9. (But here's the good news: Authenticated requests get a higher rate limit.)","documentation_url":"https://docs.github.com/rest/overview/resources-in-the-rest-api#rate-limiting"}
        """;
}

internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static StubHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return respond(request, cancellationToken);
    }
}

public sealed class GitHubUpdateCheckerTests
{
    private static async Task<UpdateCheckResult> Check(string current, StubHandler handler, TimeSpan? timeout = null)
    {
        using var checker = new GitHubUpdateChecker(current, handler, timeout);
        return await checker.CheckAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_newer_Windows_release_is_offered_and_the_Mac_pre_release_and_draft_ones_are_ignored()
    {
        UpdateCheckResult result = await Check("0.1.0", StubHandler.Json(ReleaseFixtures.Mixed));

        Assert.Equal(UpdateOutcome.Available, result.Outcome);
        UpdateInfo info = result.Update!;
        Assert.Equal("0.2.0", info.Version);
        Assert.Equal("windows-v0.2.0", info.Tag);
        Assert.Equal("https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v0.2.0", info.PageUrl.AbsoluteUri);
        Assert.Equal("Tray icon, scheduled scans, alerts.", info.Notes);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), info.PublishedAt);
        Assert.Equal("Version 0.2.0 is available.", result.Message);
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.2.1")]
    [InlineData("1.0.0")]
    public async Task Being_on_or_past_the_latest_release_is_up_to_date(string current)
    {
        UpdateCheckResult result = await Check(current, StubHandler.Json(ReleaseFixtures.Mixed));

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task Versions_compare_by_number_not_by_text()
    {
        UpdateCheckResult result = await Check("0.9.0", StubHandler.Json(ReleaseFixtures.TenIsNewerThanNine));

        Assert.Equal("0.10.0", result.Update?.Version);
    }

    [Fact]
    public async Task A_build_suffix_on_the_running_version_does_not_matter()
    {
        UpdateCheckResult result = await Check("0.2.0+abcdef1", StubHandler.Json(ReleaseFixtures.Mixed));

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
    }

    [Theory]
    [InlineData(ReleaseFixtures.OnlyMac)]
    [InlineData("[]")]
    public async Task No_Windows_release_means_nothing_to_offer(string body)
    {
        UpdateCheckResult result = await Check("0.1.0", StubHandler.Json(body));

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task A_page_address_that_is_not_https_github_com_is_replaced_by_the_tag_page()
    {
        UpdateCheckResult result = await Check("0.1.0", StubHandler.Json(ReleaseFixtures.OddPage));

        Assert.Equal("https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v0.2.0", result.Update!.PageUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ReleaseFixtures.RateLimited, "limiting")]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", "limiting")]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}", "404")]
    [InlineData(HttpStatusCode.InternalServerError, "oops", "500")]
    [InlineData(HttpStatusCode.BadGateway, "", "502")]
    public async Task Rate_limits_and_server_errors_are_a_message_not_an_exception(HttpStatusCode status, string body, string mention)
    {
        UpdateCheckResult result = await Check("0.1.0", StubHandler.Json(body, status));

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains(mention, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"message\":\"an object, not a list\"}")]
    [InlineData("")]
    public async Task An_answer_that_is_not_a_release_list_is_a_failure(string body)
    {
        UpdateCheckResult result = await Check("0.1.0", StubHandler.Json(body));

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Offline_is_a_failure_with_a_helpful_message()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("No such host is known."));

        UpdateCheckResult result = await Check("0.1.0", handler);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("Couldn't reach GitHub", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_slow_answer_times_out_into_a_failure()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        UpdateCheckResult result = await Check("0.1.0", handler, TimeSpan.FromMilliseconds(100));

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Cancelling_is_the_only_thing_that_throws()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var checker = new GitHubUpdateChecker("0.1.0", handler);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync(cancel.Token));
    }

    [Fact]
    public async Task A_version_that_cannot_be_read_cannot_be_compared()
    {
        UpdateCheckResult result = await Check("dev", StubHandler.Json(ReleaseFixtures.Mixed));

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task The_request_is_one_plain_GET_that_says_nothing_about_the_PC()
    {
        StubHandler handler = StubHandler.Json(ReleaseFixtures.Mixed);

        await Check("0.1.0+deadbeef", handler);

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/karuneshpalekar/DevSweep/releases?per_page=30", request.RequestUri!.AbsoluteUri);
        Assert.Equal("?per_page=30", request.RequestUri.Query);
        Assert.Null(request.Content);
        string[] headers = [.. request.Headers.Select(h => h.Key).Order(StringComparer.OrdinalIgnoreCase)];
        Assert.Equal(["Accept", "User-Agent", "X-GitHub-Api-Version"], headers);
        Assert.Equal("DevSweep/0.1.0", request.Headers.UserAgent.ToString());
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(request.Headers.Contains("Cookie"));
    }

    [Theory]
    [InlineData("windows-v1.2.3", "windows-v", "1.2.3")]
    [InlineData("windows-v1.2", "windows-v", "1.2")]
    [InlineData("windows-v1.2.3-beta.1", "windows-v", "1.2.3")]
    [InlineData("v0.9.6", null, "0.9.6")]
    [InlineData("0.1.0+abc", null, "0.1.0")]
    public void Versions_parse(string text, string? prefix, string expected)
        => Assert.Equal(expected, AppVersion.Parse(text, prefix)?.ToString());

    [Theory]
    [InlineData("v0.9.6", "windows-v")]
    [InlineData("windows-vx", "windows-v")]
    [InlineData("", null)]
    [InlineData("dev", null)]
    public void Things_that_are_not_versions_do_not_parse(string text, string? prefix)
        => Assert.Null(AppVersion.Parse(text, prefix));

    [Fact]
    public void Versions_order_numerically_and_trailing_zeros_do_not_count()
    {
        Assert.True(AppVersion.Parse("0.10.0")! > AppVersion.Parse("0.9.9")!);
        Assert.True(AppVersion.Parse("1.0")! == AppVersion.Parse("1.0.0")!);
        Assert.Equal(AppVersion.Parse("1.0")!.GetHashCode(), AppVersion.Parse("1.0.0")!.GetHashCode());
        Assert.True(AppVersion.Parse("2.0.0")! > AppVersion.Parse("1.99.99")!);
    }
}

internal sealed class QueueChecker(params UpdateCheckResult[] results) : IUpdateChecker
{
    private readonly Queue<UpdateCheckResult> _results = new(results);

    public int Calls { get; private set; }

    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_results.Count > 1 ? _results.Dequeue() : _results.Peek());
    }

    public static UpdateCheckResult Found(string version) => new(UpdateOutcome.Available,
        new UpdateInfo(version, "windows-v" + version, new Uri("https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v" + version), "notes", null), $"Version {version} is available.");

    public static UpdateCheckResult Same { get; } = UpdateCheckResult.UpToDate("0.1.0");

    public static UpdateCheckResult Down { get; } = UpdateCheckResult.Failed("Couldn't reach GitHub.");
}

public sealed class UpdateViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private static (UpdateViewModel Vm, AppSettingsStore Settings, Func<DateTimeOffset> Clock, Action<TimeSpan> Advance) Create(QueueChecker checker)
    {
        DateTimeOffset now = T0;
        var settings = new AppSettingsStore(null);
        var vm = new UpdateViewModel(checker, settings, () => now);
        return (vm, settings, () => now, d => now += d);
    }

    [Fact]
    public async Task It_checks_about_once_a_day()
    {
        var checker = new QueueChecker(QueueChecker.Same);
        (UpdateViewModel vm, _, _, Action<TimeSpan> advance) = Create(checker);

        await vm.CheckIfDueAsync();
        Assert.Equal(1, checker.Calls);
        advance(TimeSpan.FromHours(19));
        await vm.CheckIfDueAsync();
        Assert.Equal(1, checker.Calls);
        advance(TimeSpan.FromHours(2));
        await vm.CheckIfDueAsync();
        Assert.Equal(2, checker.Calls);
    }

    [Fact]
    public async Task Turned_off_it_never_asks()
    {
        var checker = new QueueChecker(QueueChecker.Found("0.2.0"));
        (UpdateViewModel vm, AppSettingsStore settings, _, _) = Create(checker);
        settings.Update(s => s with { CheckForUpdates = false });

        await vm.CheckIfDueAsync();

        Assert.Equal(0, checker.Calls);
        Assert.Null(vm.Available);
    }

    [Fact]
    public async Task A_failure_is_quiet_keeps_what_was_known_and_is_retried_no_sooner_than_two_hours_later()
    {
        var checker = new QueueChecker(QueueChecker.Found("0.2.0"), QueueChecker.Down);
        (UpdateViewModel vm, AppSettingsStore settings, _, Action<TimeSpan> advance) = Create(checker);
        await vm.CheckIfDueAsync();
        Assert.True(vm.ShowCard);
        DateTimeOffset? good = settings.Current.LastUpdateCheck;

        advance(TimeSpan.FromHours(21));
        await vm.CheckIfDueAsync();   // fails

        Assert.Equal(2, checker.Calls);
        Assert.True(vm.ShowCard);                               // the card from the earlier check stays
        Assert.Equal("Version 0.2.0 is available.", vm.Status); // a background failure does not replace the status
        Assert.Equal(good, settings.Current.LastUpdateCheck);   // a failure is not a successful check
        advance(TimeSpan.FromHours(1));
        await vm.CheckIfDueAsync();
        Assert.Equal(2, checker.Calls);                         // too soon after the failed attempt
        advance(TimeSpan.FromHours(1.5));
        await vm.CheckIfDueAsync();
        Assert.Equal(3, checker.Calls);
    }

    [Fact]
    public async Task Check_now_ignores_the_daily_limit_and_says_why_it_failed()
    {
        var checker = new QueueChecker(QueueChecker.Same, QueueChecker.Down);
        (UpdateViewModel vm, _, _, _) = Create(checker);

        await vm.CheckNowAsync();
        Assert.Equal("You're up to date (version 0.1.0).", vm.Status);
        await vm.CheckNowAsync();
        Assert.Equal("Couldn't reach GitHub.", vm.Status);
        Assert.Equal(2, checker.Calls);
    }

    [Fact]
    public async Task The_card_shows_for_a_newer_release_and_Later_hides_that_version_only()
    {
        var checker = new QueueChecker(QueueChecker.Found("0.2.0"), QueueChecker.Found("0.3.0"));
        (UpdateViewModel vm, AppSettingsStore settings, _, _) = Create(checker);
        await vm.CheckNowAsync();
        Assert.True(vm.ShowCard);

        vm.Later();
        Assert.False(vm.ShowCard);
        Assert.Equal("0.2.0", settings.Current.SkippedUpdate);

        await vm.CheckNowAsync();   // 0.3.0 is a different version
        Assert.True(vm.ShowCard);
        Assert.Equal("0.3.0", vm.Available!.Version);
    }

    [Fact]
    public async Task The_card_goes_when_the_setting_is_turned_off()
    {
        var checker = new QueueChecker(QueueChecker.Found("0.2.0"));
        (UpdateViewModel vm, AppSettingsStore settings, _, _) = Create(checker);
        await vm.CheckNowAsync();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.Update(s => s with { CheckForUpdates = false });

        Assert.False(vm.ShowCard);
        Assert.Contains(nameof(UpdateViewModel.ShowCard), changed);
    }

    [Fact]
    public async Task Open_asks_the_app_to_open_the_release_page()
    {
        var checker = new QueueChecker(QueueChecker.Found("0.2.0"));
        (UpdateViewModel vm, _, _, _) = Create(checker);
        await vm.CheckNowAsync();
        Uri? opened = null;
        vm.OpenRequested += (_, url) => opened = url;

        vm.OpenRelease();

        Assert.Equal("https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v0.2.0", opened?.AbsoluteUri);
    }

    [Fact]
    public async Task What_the_last_check_found_survives_in_the_settings_file()
    {
        string file = Path.Join(Path.GetTempPath(), "DevSweep-update-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettingsStore(file);
            var vm = new UpdateViewModel(new QueueChecker(QueueChecker.Found("0.2.0")), settings, () => T0);
            await vm.CheckNowAsync();
            vm.Later();

            var reloaded = new AppSettingsStore(file);

            Assert.Equal(T0, reloaded.Current.LastUpdateCheck);
            Assert.Equal("0.2.0", reloaded.Current.SkippedUpdate);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
