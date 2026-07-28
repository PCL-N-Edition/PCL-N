// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>JSON-RPC-ish frames exchanged with PCL.Plugin.Sidecar (AOT-friendly DTOs).</summary>
internal static class PluginSidecarMethods
{
    public const string SystemHello = "system.hello";
    public const string SystemShutdown = "system.shutdown";
    public const string HealthPing = "health.ping";
    public const string RuntimeInit = "runtime.init";
    public const string UiOpenSettings = "ui.openSettings";
    public const string CatalogList = "catalog.list";
    public const string CatalogInstallPnp = "catalog.installPnp";
    public const string CatalogSetEnabled = "catalog.setEnabled";
    public const string CatalogUninstall = "catalog.uninstall";
    public const string RuntimeStatus = "runtime.status";
}

internal sealed class PluginSidecarRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public PluginSidecarParams? Params { get; set; }
}

internal sealed class PluginSidecarResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("result")]
    public PluginSidecarResult? Result { get; set; }

    [JsonPropertyName("error")]
    public PluginSidecarError? Error { get; set; }
}

internal sealed class PluginSidecarParams
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("applicationDataDirectory")]
    public string? ApplicationDataDirectory { get; set; }

    [JsonPropertyName("cacheDirectory")]
    public string? CacheDirectory { get; set; }

    [JsonPropertyName("hostVersion")]
    public string? HostVersion { get; set; }

    [JsonPropertyName("pageId")]
    public string? PageId { get; set; }

    [JsonPropertyName("packagePath")]
    public string? PackagePath { get; set; }

    [JsonPropertyName("pluginId")]
    public string? PluginId { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}

internal sealed class PluginSidecarResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("sidecarVersion")]
    public string? SidecarVersion { get; set; }

    [JsonPropertyName("plugins")]
    public PluginSidecarCatalogEntry[]? Plugins { get; set; }

    [JsonPropertyName("runtimeRoot")]
    public string? RuntimeRoot { get; set; }

    [JsonPropertyName("installedCount")]
    public int InstalledCount { get; set; }

    [JsonPropertyName("enabledCount")]
    public int EnabledCount { get; set; }
}

internal sealed class PluginSidecarError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

internal sealed class PluginSidecarCatalogEntry
{
    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}
