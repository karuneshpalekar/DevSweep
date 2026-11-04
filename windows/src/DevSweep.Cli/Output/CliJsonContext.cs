using System.Text.Json.Serialization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform.Quarantine;

namespace DevSweep.Cli.Output;

/// <summary>Source-generated JSON metadata: no reflection, so Native AOT and trimming stay safe.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MeasureReport))]
[JsonSerializable(typeof(List<QuarantineEntry>))]
[JsonSerializable(typeof(List<HistoryEntry>))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
