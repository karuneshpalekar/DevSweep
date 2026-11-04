namespace DevSweep.Core.Rules;

/// <summary>
/// Where a finding is listed in Clean up. The Mac categories (less Xcode),
/// plus Windows ones: .NET, Visual Studio, WSL and Windows itself.
/// YAML names are camelCase (<c>packageCaches</c>, <c>visualStudio</c>).
/// </summary>
public enum Category
{
    Leftovers,
    IdeVersions,
    Android,
    Toolchains,
    PackageCaches,
    Browsers,
    Projects,
    AiModels,
    BackgroundServices,
    LargeFiles,
    Docker,
    Backups,
    Dotnet,
    VisualStudio,
    Wsl,
    Windows,
}

/// <summary>Names and titles of <see cref="Category"/> values.</summary>
public static class Categories
{
    public static string Title(this Category category) => category switch
    {
        Category.Leftovers => "Leftovers",
        Category.IdeVersions => "Old IDE versions",
        Category.Android => "Android",
        Category.Toolchains => "Toolchains",
        Category.PackageCaches => "Package caches",
        Category.Browsers => "Browsers",
        Category.Projects => "Projects",
        Category.AiModels => "AI models",
        Category.BackgroundServices => "Background services",
        Category.LargeFiles => "Large files",
        Category.Docker => "Docker",
        Category.Backups => "Backups",
        Category.Dotnet => ".NET",
        Category.VisualStudio => "Visual Studio",
        Category.Wsl => "WSL",
        Category.Windows => "Windows",
        _ => category.ToString(),
    };

    /// <summary>The YAML name: <c>packageCaches</c>.</summary>
    public static string YamlName(this Category category)
    {
        string name = category.ToString();
        return string.Concat(char.ToLowerInvariant(name[0]).ToString(), name.AsSpan(1));
    }

    /// <summary>Parses a YAML name (case-insensitive).</summary>
    public static bool TryParse(string? name, out Category category)
    {
        foreach (Category value in Enum.GetValues<Category>())
        {
            if (string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                category = value;
                return true;
            }
        }

        category = default;
        return false;
    }

    /// <summary>Every YAML name, for error messages.</summary>
    public static string AllYamlNames => string.Join(", ", Enum.GetValues<Category>().Select(YamlName));
}
