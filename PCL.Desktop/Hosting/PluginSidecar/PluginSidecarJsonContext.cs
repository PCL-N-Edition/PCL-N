// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace PCL.Desktop.Hosting.PluginSidecar;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PluginSidecarRequest))]
[JsonSerializable(typeof(PluginSidecarResponse))]
[JsonSerializable(typeof(PluginSidecarV4Request))]
[JsonSerializable(typeof(PluginSidecarV4Response))]
[JsonSerializable(typeof(PluginSidecarParams))]
[JsonSerializable(typeof(PluginSidecarResult))]
[JsonSerializable(typeof(PluginSidecarError))]
[JsonSerializable(typeof(PluginSidecarProgress))]
[JsonSerializable(typeof(PluginSidecarHostInstance))]
[JsonSerializable(typeof(PluginSidecarHostInstance[]))]
[JsonSerializable(typeof(PluginSidecarGameSession))]
[JsonSerializable(typeof(PluginSidecarGameSession[]))]
[JsonSerializable(typeof(PluginSidecarCatalogEntry))]
[JsonSerializable(typeof(PluginSidecarCatalogEntry[]))]
[JsonSerializable(typeof(PluginUiGroupDto))]
[JsonSerializable(typeof(PluginUiGroupDto[]))]
[JsonSerializable(typeof(PluginUiPageDto))]
[JsonSerializable(typeof(PluginUiPageDto[]))]
[JsonSerializable(typeof(PluginUiNavigationGroupDto))]
[JsonSerializable(typeof(PluginUiNavigationGroupDto[]))]
[JsonSerializable(typeof(PluginUiNavigationItemDto))]
[JsonSerializable(typeof(PluginUiNavigationItemDto[]))]
[JsonSerializable(typeof(PluginUiNodeDto))]
[JsonSerializable(typeof(PluginUiNodeDto[]))]
[JsonSerializable(typeof(PluginUiOptionDto))]
[JsonSerializable(typeof(PluginUiOptionDto[]))]
[JsonSerializable(typeof(PluginUiStorageSegmentDto))]
[JsonSerializable(typeof(PluginUiStorageSegmentDto[]))]
[JsonSerializable(typeof(PluginSidecarIssueCategoryDto))]
[JsonSerializable(typeof(PluginSidecarIssueCategoryDto[]))]
[JsonSerializable(typeof(string[]))]
internal partial class PluginSidecarJsonContext : JsonSerializerContext;
