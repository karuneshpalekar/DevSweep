using System.Text.Json.Serialization;

namespace DevSweep.Core.Projects.GitHub;

/// <summary>Source-generated JSON for the GitHub REST API (snake_case): no reflection, AOT and trim safe.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<GitHubRepoDto>))]
[JsonSerializable(typeof(GitHubRepoDto))]
[JsonSerializable(typeof(GitHubUserDto))]
[JsonSerializable(typeof(GitHubErrorDto))]
[JsonSerializable(typeof(GitHubCreateRepoDto))]
public sealed partial class GitHubJsonContext : JsonSerializerContext;
