using System.Text.Json.Serialization;

namespace PCL.Services.Updates;

/// <summary>
/// Source-generated JSON contract for the update block map family: property names are the
/// file contract and stay case-insensitive-compatible with the legacy updater.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UpdateBlockMap))]
[JsonSerializable(typeof(UpdateChunkingParameters))]
[JsonSerializable(typeof(UpdateBlockFull))]
[JsonSerializable(typeof(UpdateBlockDelta))]
[JsonSerializable(typeof(UpdatePatchIndexDto))]
[JsonSerializable(typeof(UpdatePatchStrategyDto))]
[JsonSerializable(typeof(UpdatePatchVariantDto))]
[JsonSerializable(typeof(UpdatePatchDto))]
public sealed partial class UpdateJsonContext : JsonSerializerContext;
