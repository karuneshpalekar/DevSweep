using System.Text;
using DevSweep.Core.Health.Runtimes;

namespace DevSweep.Core.Tests.Health;

/// <summary>
/// endoflife.date responses recorded in the API's format (trimmed to the fields DevSweep reads),
/// so no test ever touches the network. "Today" in these tests is 4 Nov 2026.
/// </summary>
internal static class EolFixtures
{
    public static readonly DateOnly Today = new(2026, 11, 4);

    public const string NodeJs = """
        [
          {"cycle":"25","releaseDate":"2025-10-15","eol":"2026-06-01","latest":"25.1.0","lts":false},
          {"cycle":"24","releaseDate":"2025-05-06","eol":"2028-04-30","latest":"24.11.0","lts":"2025-10-28"},
          {"cycle":"22","releaseDate":"2024-04-24","eol":"2027-04-30","latest":"22.21.1","lts":"2024-10-29"},
          {"cycle":"20","releaseDate":"2023-04-18","eol":"2026-04-30","latest":"20.19.5","lts":"2023-10-24"},
          {"cycle":"18","releaseDate":"2022-04-19","eol":"2025-04-30","latest":"18.20.8","lts":"2022-10-25"},
          {"cycle":"16","releaseDate":"2021-04-20","eol":"2023-09-11","latest":"16.20.2","lts":"2021-10-26"}
        ]
        """;

    public const string Python = """
        [
          {"cycle":"3.14","releaseDate":"2025-10-07","eol":"2030-10-31","latest":"3.14.0","lts":false},
          {"cycle":"3.13","releaseDate":"2024-10-07","eol":"2029-10-31","latest":"3.13.9","lts":false},
          {"cycle":"3.12","releaseDate":"2023-10-02","eol":"2028-10-31","latest":"3.12.12","lts":false},
          {"cycle":"3.11","releaseDate":"2022-10-24","eol":"2027-10-31","latest":"3.11.14","lts":false},
          {"cycle":"3.10","releaseDate":"2021-10-04","eol":"2026-10-31","latest":"3.10.19","lts":false},
          {"cycle":"3.9","releaseDate":"2020-10-05","eol":"2025-10-31","latest":"3.9.24","lts":false}
        ]
        """;

    /// <summary>Go: a release line whose end is within 120 days of "today", and a number as a cycle.</summary>
    public const string Go = """
        [
          {"cycle":"1.25","releaseDate":"2025-08-12","eol":false,"latest":"1.25.3"},
          {"cycle":"1.24","releaseDate":"2025-02-11","eol":"2027-01-15","latest":"1.24.9"},
          {"cycle":1.23,"releaseDate":"2024-08-13","eol":true,"latest":"1.23.12"}
        ]
        """;

    public const string Dotnet = """
        [
          {"cycle":"10.0","releaseDate":"2025-11-11","eol":"2028-11-14","latest":"10.0.0","lts":true},
          {"cycle":"9.0","releaseDate":"2024-11-12","eol":"2026-11-10","latest":"9.0.11","lts":false},
          {"cycle":"8.0","releaseDate":"2023-11-14","eol":"2026-11-10","latest":"8.0.22","lts":true},
          {"cycle":"6.0","releaseDate":"2021-11-08","eol":"2024-11-12","latest":"6.0.36","lts":true}
        ]
        """;

    public const string Temurin = """
        [
          {"cycle":"25","releaseDate":"2025-09-16","eol":"2031-09-30","latest":"25.0.1+8","lts":true},
          {"cycle":"21","releaseDate":"2023-09-19","eol":"2029-12-31","latest":"21.0.9+10","lts":true},
          {"cycle":"17","releaseDate":"2021-09-14","eol":"2027-10-31","latest":"17.0.17+10","lts":true},
          {"cycle":"8","releaseDate":"2014-03-18","eol":"2026-11-30","latest":"8u472-b08","lts":true}
        ]
        """;

    public static IReadOnlyList<ReleaseCycle> Cycles(string json) => ReleaseCycle.ParseList(Encoding.UTF8.GetBytes(json))!;

    public static EolData Data(string json) => new(Cycles(json), new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero), false);

    /// <summary>A fetcher answering from recorded responses; counts requests.</summary>
    public sealed class Recorded : IEolFetcher
    {
        public Dictionary<string, string> Responses { get; } = new(StringComparer.Ordinal)
        {
            ["nodejs"] = NodeJs, ["python"] = Python, ["go"] = Go, ["dotnet"] = Dotnet, ["eclipse-temurin"] = Temurin,
        };

        public List<string> Requests { get; } = [];

        public Task<byte[]?> FetchAsync(string product, CancellationToken cancellationToken)
        {
            Requests.Add(product);
            return Task.FromResult(Responses.TryGetValue(product, out string? json) ? Encoding.UTF8.GetBytes(json) : null);
        }
    }
}
