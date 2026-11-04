using System.Globalization;
using DevSweep.Core.Rules.Yaml;

namespace DevSweep.Core.Rules;

/// <summary>One problem in a rule pack, with where it is.</summary>
/// <param name="Pack">The pack's file name (or resource name).</param>
/// <param name="RuleId">The rule it is in, when known.</param>
/// <param name="Line">1-based line, 0 when not known.</param>
/// <param name="Column">1-based column, 0 when not known.</param>
/// <param name="Message">What is wrong, in words.</param>
public sealed record RuleError(string Pack, string? RuleId, int Line, int Column, string Message)
{
    /// <summary>"npm.yml:12:5: rule npm-cache: unknown detector kind "pathz"..."</summary>
    public override string ToString()
    {
        string where = Line > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Pack}:{Line}:{Column}") : Pack;
        return RuleId is null ? $"{where}: {Message}" : $"{where}: rule {RuleId}: {Message}";
    }
}

/// <summary>Collects <see cref="RuleError"/>s while a pack is read, so every problem is reported at once.</summary>
public sealed class RuleErrorSink(string pack)
{
    private readonly List<RuleError> _errors = [];

    public string Pack { get; } = pack;

    /// <summary>The rule being read, for messages.</summary>
    public string? RuleId { get; set; }

    public IReadOnlyList<RuleError> Errors => _errors;

    public bool HasErrors => _errors.Count > 0;

    public void Add(YamlNode? at, string message)
        => _errors.Add(new RuleError(Pack, RuleId, at?.Line ?? 0, at?.Column ?? 0, message));

    public void Add(int line, int column, string message) => _errors.Add(new RuleError(Pack, RuleId, line, column, message));
}

/// <summary>The YAML text itself could not be parsed.</summary>
public sealed class RuleSyntaxException : Exception
{
    public RuleSyntaxException()
    {
    }

    public RuleSyntaxException(string message)
        : base(message)
    {
    }

    public RuleSyntaxException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RuleSyntaxException(int line, int column, string message)
        : base(message)
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }

    public int Column { get; }
}

/// <summary>A rule built in code was used without passing schema validation.</summary>
public sealed class RuleSchemaException : Exception
{
    public RuleSchemaException()
    {
    }

    public RuleSchemaException(string message)
        : base(message)
    {
    }

    public RuleSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RuleSchemaException(IReadOnlyList<RuleError> errors)
        : base(string.Join(Environment.NewLine, errors ?? []))
    {
        Errors = errors ?? [];
    }

    public IReadOnlyList<RuleError> Errors { get; } = [];
}
