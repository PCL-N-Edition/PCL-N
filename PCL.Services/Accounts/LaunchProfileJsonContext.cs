using System.Text.Json.Serialization;

namespace PCL.Services.Accounts;

/// <summary>
/// Source-generated JSON contract for the legacy launch profile file: camelCase property
/// names, indented output, and string enum values.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(LaunchProfile))]
[JsonSerializable(typeof(LaunchProfileSet))]
internal sealed partial class LaunchProfileJsonContext : JsonSerializerContext;
