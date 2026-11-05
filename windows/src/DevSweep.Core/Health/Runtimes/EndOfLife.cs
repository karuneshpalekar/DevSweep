using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevSweep.Core.Storage;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>One release line from endoflife.date.</summary>
/// <param name="Cycle">"20", "3.12", "8.0".</param>
/// <param name="ReleaseDate">First release of the line.</param>
/// <param name="EolDate">End of security support; null when none is announced or only a yes/no is published.</param>
/// <param name="EolFlag">The published yes/no when there is no date.</param>
/// <param name="Latest">Newest patch release in the line.</param>
/// <param name="LtsFrom">The line is LTS from this day (or always, when <see cref="LtsFlag"/>).</param>
/// <param name="LtsFlag">The published LTS yes/no when there is no date.</param>
public sealed record ReleaseCycle(string Cycle, DateOnly? ReleaseDate, DateOnly? EolDate, bool EolFlag, string? Latest, DateOnly? LtsFrom, bool LtsFlag)
{
    /// <summary>"Ending soon" means support ends within this many days.</summary>
    public const int EndingSoonDays = 120;

    public bool Ended(DateOnly today) => EolDate is { } eol ? eol <= today : EolFlag;

    public bool IsLts(DateOnly today) => LtsFrom is { } from ? from <= today : LtsFlag;

    public SupportStatus Status(DateOnly today)
    {
        if (Ended(today))
        {
            return SupportStatus.EndOfLife;
        }

        return EolDate is { } eol && eol.DayNumber - today.DayNumber < EndingSoonDays ? SupportStatus.EndingSoon : SupportStatus.Supported;
    }

    /// <summary>Parses an endoflife.date product file (<c>/api/&lt;product&gt;.json</c>); null when it is not one.</summary>
    public static IReadOnlyList<ReleaseCycle>? ParseList(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var cycles = new List<ReleaseCycle>();
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("cycle", out JsonElement cycle))
                {
                    continue;
                }

                string name = cycle.ValueKind switch
                {
                    JsonValueKind.String => cycle.GetString() ?? string.Empty,
                    JsonValueKind.Number => cycle.GetDouble().ToString(CultureInfo.InvariantCulture),
                    _ => string.Empty,
                };
                (DateOnly? eol, bool eolFlag) = DateOrBool(e, "eol");
                (DateOnly? lts, bool ltsFlag) = DateOrBool(e, "lts");
                (DateOnly? release, _) = DateOrBool(e, "releaseDate");
                string? latest = e.TryGetProperty("latest", out JsonElement l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                cycles.Add(new ReleaseCycle(name, release, eol, eolFlag, latest, lts, ltsFlag));
            }

            return cycles;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (DateOnly?, bool) DateOrBool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement value))
        {
            return (null, false);
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => (null, true),
            JsonValueKind.False => (null, false),
            JsonValueKind.String when DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) => (d, false),
            _ => (null, false),
        };
    }
}

/// <summary>Support data for one product, and how fresh it is.</summary>
/// <param name="Cycles">Release lines, newest first (as published); empty when unknown.</param>
/// <param name="Fetched">When the data was downloaded; null when there is none.</param>
/// <param name="Stale">Came from an old cache because the download failed (or nothing at all).</param>
public sealed record EolData(IReadOnlyList<ReleaseCycle> Cycles, DateTimeOffset? Fetched, bool Stale);

/// <summary>Downloads one product's support data. Faked in tests (recorded fixtures, never the network).</summary>
public interface IEolFetcher
{
    /// <summary>The response body, or null on any failure.</summary>
    Task<byte[]?> FetchAsync(string product, CancellationToken cancellationToken);
}

/// <summary>Never fetches (offline mode).</summary>
public sealed class OfflineEolFetcher : IEolFetcher
{
    public static OfflineEolFetcher Instance { get; } = new();

    public Task<byte[]?> FetchAsync(string product, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
}

/// <summary><c>https://endoflife.date/api/&lt;product&gt;.json</c>, 10-second limit, any failure is "no data".</summary>
public sealed class HttpEolFetcher : IEolFetcher, IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public HttpEolFetcher()
    {
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DevSweep", "1.0"));
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/karuneshpalekar/DevSweep)"));
    }

    public async Task<byte[]?> FetchAsync(string product, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(new Uri($"https://endoflife.date/api/{product}.json"), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Support schedules from endoflife.date, cached for a day in
/// <c>%LOCALAPPDATA%\DevSweep\endoflife</c> so Health works offline with the last
/// data it had. The network is optional: every failure falls back to the cache, then to "not checked".
/// </summary>
public sealed partial class EndOfLifeClient(string? cacheDirectory, IEolFetcher fetcher, Func<DateTimeOffset>? now = null)
{
    public static TimeSpan MaxAge { get; } = TimeSpan.FromDays(1);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, EolData> _memo = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public string? CacheDirectory { get; } = cacheDirectory;

    /// <summary>Some product's data came from an old cache or was missing.</summary>
    public bool UsedStaleData { get; private set; }

    public async Task<EolData> CyclesAsync(string product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (!ProductName().IsMatch(product))
        {
            return new EolData([], null, true);
        }

        lock (_lock)
        {
            if (_memo.TryGetValue(product, out EolData? memo))
            {
                return memo;
            }
        }

        EolData data = await LoadAsync(product, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _memo[product] = data;
            UsedStaleData |= data.Stale;
        }

        return data;
    }

    private async Task<EolData> LoadAsync(string product, CancellationToken cancellationToken)
    {
        string? file = CacheDirectory is null ? null : Path.Join(CacheDirectory, product + ".json");
        (IReadOnlyList<ReleaseCycle>? cached, DateTimeOffset? cachedAt) = ReadCache(file);
        if (cached is not null && cachedAt is { } at && _now() - at < MaxAge)
        {
            return new EolData(cached, at, false);
        }

        byte[]? body = await fetcher.FetchAsync(product, cancellationToken).ConfigureAwait(false);
        if (body is not null && ReleaseCycle.ParseList(body) is { } fresh)
        {
            if (file is not null)
            {
                try
                {
                    AtomicFile.Write(file, body);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The data is still used; only the cache could not be kept.
                }
            }

            return new EolData(fresh, _now(), false);
        }

        return cached is not null ? new EolData(cached, cachedAt, true) : new EolData([], null, true);
    }

    private static (IReadOnlyList<ReleaseCycle>?, DateTimeOffset?) ReadCache(string? file)
    {
        if (file is null || !File.Exists(file))
        {
            return (null, null);
        }

        try
        {
            return (ReleaseCycle.ParseList(File.ReadAllBytes(file)), new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ProductName();
}
