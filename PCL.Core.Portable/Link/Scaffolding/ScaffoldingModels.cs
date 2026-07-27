// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace PCL.Core.Link.Scaffolding.Client.Models
{
    public sealed record LobbyInfo(string FullCode, string NetworkName, string NetworkSecret);

    public enum PlayerKind
    {
        HOST,
        GUEST
    }

    public sealed record PlayerProfile
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("machine_id")]
        public required string MachineId { get; init; }

        [JsonPropertyName("vendor")]
        public required string Vendor { get; init; }

        [JsonPropertyName("kind")]
        [JsonConverter(typeof(JsonStringEnumConverter<PlayerKind>))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PlayerKind? Kind { get; init; }
    }

    [JsonSerializable(typeof(PlayerProfile))]
    [JsonSerializable(typeof(PlayerProfile[]))]
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    internal sealed partial class ScaffoldingJsonContext : JsonSerializerContext;
}
