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
[JsonSerializable(typeof(PluginSidecarParams))]
[JsonSerializable(typeof(PluginSidecarResult))]
[JsonSerializable(typeof(PluginSidecarError))]
[JsonSerializable(typeof(PluginSidecarProgress))]
[JsonSerializable(typeof(PluginSidecarCatalogEntry))]
[JsonSerializable(typeof(PluginSidecarCatalogEntry[]))]
[JsonSerializable(typeof(PluginUiGroupDto))]
[JsonSerializable(typeof(PluginUiGroupDto[]))]
[JsonSerializable(typeof(PluginUiPageDto))]
[JsonSerializable(typeof(PluginUiPageDto[]))]
[JsonSerializable(typeof(PluginUiNodeDto))]
[JsonSerializable(typeof(PluginUiNodeDto[]))]
[JsonSerializable(typeof(PluginUiOptionDto))]
[JsonSerializable(typeof(PluginUiOptionDto[]))]
[JsonSerializable(typeof(PluginSidecarIssueCategoryDto))]
[JsonSerializable(typeof(PluginSidecarIssueCategoryDto[]))]
[JsonSerializable(typeof(string[]))]
internal partial class PluginSidecarJsonContext : JsonSerializerContext;
