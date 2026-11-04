using System.Text.Json.Serialization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform.Quarantine;

namespace DevSweep.Core.Storage;

/// <summary>Source-generated JSON for everything the engine stores: no reflection, AOT and trim safe.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<HistoryEntry>))]
[JsonSerializable(typeof(QuarantineEntry))]
public sealed partial class CoreJsonContext : JsonSerializerContext;
