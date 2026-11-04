using System.Text.Json.Serialization;

namespace DevSweep.Cli.Output;

/// <summary>Source-generated JSON metadata: no reflection, so Native AOT and trimming stay safe.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(MeasureReport))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
