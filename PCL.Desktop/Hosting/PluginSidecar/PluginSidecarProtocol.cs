// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>JSON-RPC frames + UI data-chain DTOs (AOT-friendly).</summary>
internal static class PluginSidecarMethods
{
    public const string SystemHello = "system.hello";
    public const string SystemShutdown = "system.shutdown";
    public const string HealthPing = "health.ping";
    public const string RuntimeInit = "runtime.init";
    public const string RuntimeStatus = "runtime.status";
    public const string CatalogList = "catalog.list";
    public const string CatalogInstallPnp = "catalog.installPnp";
    public const string CatalogSetEnabled = "catalog.setEnabled";
    public const string CatalogUninstall = "catalog.uninstall";
    /// <summary>Data-chain: groups + pages to inject into host settings.</summary>
    public const string UiManifest = "ui.manifest";
    /// <summary>Data-chain: full page body as node tree.</summary>
    public const string UiGetPage = "ui.getPage";
    /// <summary>Data-chain: invoke an action declared on a node.</summary>
    public const string UiInvokeAction = "ui.invokeAction";
    public const string FeedbackSession = "feedback.session";
    public const string FeedbackCatalog = "feedback.catalog";
    public const string FeedbackSubmit = "feedback.submit";
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

    /// <summary>Intermediate progress frame (same request id); final frame has result/error.</summary>
    [JsonPropertyName("progress")]
    public PluginSidecarProgress? Progress { get; set; }
}

internal sealed class PluginSidecarProgress
{
    [JsonPropertyName("stage")]
    public string Stage { get; set; } = "";

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("completedFiles")]
    public int CompletedFiles { get; set; }

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("speedBytesPerSecond")]
    public long SpeedBytesPerSecond { get; set; }
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

    [JsonPropertyName("actionId")]
    public string? ActionId { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("boolValue")]
    public bool? BoolValue { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
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

    [JsonPropertyName("groups")]
    public PluginUiGroupDto[]? Groups { get; set; }

    [JsonPropertyName("pages")]
    public PluginUiPageDto[]? Pages { get; set; }

    [JsonPropertyName("root")]
    public PluginUiNodeDto? Root { get; set; }

    [JsonPropertyName("refreshPage")]
    public bool RefreshPage { get; set; }

    [JsonPropertyName("openUrl")]
    public string? OpenUrl { get; set; }

    [JsonPropertyName("pickFilePatterns")]
    public string[]? PickFilePatterns { get; set; }

    [JsonPropertyName("pickFileTitle")]
    public string? PickFileTitle { get; set; }

    [JsonPropertyName("pickFolder")]
    public bool PickFolder { get; set; }

    [JsonPropertyName("pickFolderTitle")]
    public string? PickFolderTitle { get; set; }

    /// <summary>Host should show a confirm dialog, then re-invoke the same action with boolValue=true if accepted.</summary>
    [JsonPropertyName("confirmRequired")]
    public bool ConfirmRequired { get; set; }

    [JsonPropertyName("confirmTitle")]
    public string? ConfirmTitle { get; set; }

    [JsonPropertyName("confirmBody")]
    public string? ConfirmBody { get; set; }

    [JsonPropertyName("confirmPrimary")]
    public string? ConfirmPrimary { get; set; }

    [JsonPropertyName("confirmSecondary")]
    public string? ConfirmSecondary { get; set; }

    /// <summary>Plugin id for confirm follow-up (install → approve).</summary>
    [JsonPropertyName("followUpPluginId")]
    public string? FollowUpPluginId { get; set; }

    /// <summary>Host should re-fetch ui.manifest and inject newly visible pages.</summary>
    [JsonPropertyName("refreshNavigation")]
    public bool RefreshNavigation { get; set; }

    /// <summary>Optional host launcher boolean setting key (e.g. SystemDebugMode).</summary>
    [JsonPropertyName("hostBooleanKey")]
    public string? HostBooleanKey { get; set; }

    [JsonPropertyName("hostBooleanValue")]
    public bool? HostBooleanValue { get; set; }

    [JsonPropertyName("hasSession")]
    public bool HasSession { get; set; }

    /// <summary>authenticated | anonymous</summary>
    [JsonPropertyName("sessionStatus")]
    public string? SessionStatus { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("issueCategories")]
    public PluginSidecarIssueCategoryDto[]? IssueCategories { get; set; }

    [JsonPropertyName("issueNumber")]
    public int IssueNumber { get; set; }

    [JsonPropertyName("issueUrl")]
    public string? IssueUrl { get; set; }

    [JsonPropertyName("repository")]
    public string? Repository { get; set; }
}

internal sealed class PluginSidecarIssueCategoryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("issueType")]
    public string IssueType { get; set; } = "";

    [JsonPropertyName("labels")]
    public string[] Labels { get; set; } = [];

    [JsonPropertyName("bodyTemplate")]
    public string BodyTemplate { get; set; } = "";
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

internal sealed class PluginUiGroupDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

internal sealed class PluginUiPageDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("groupId")]
    public string? GroupId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "lucide/plug";

    [JsonPropertyName("heading")]
    public string Heading { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("order")]
    public int Order { get; set; }
}

/// <summary>Declarative UI node rendered by host (data-chain injection).</summary>
internal sealed class PluginUiNodeDto
{
    /// <summary>card | text | muted | button | checkbox | stack | list | row | hint | textbox | select</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "stack";

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("actionId")]
    public string? ActionId { get; set; }

    [JsonPropertyName("checked")]
    public bool? Checked { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("children")]
    public PluginUiNodeDto[]? Children { get; set; }

    [JsonPropertyName("meta")]
    public string? Meta { get; set; }

    /// <summary>Watermark for textbox nodes.</summary>
    [JsonPropertyName("placeholder")]
    public string? Placeholder { get; set; }

    /// <summary>On button click, send current text of this field id as <c>value</c>.</summary>
    [JsonPropertyName("valueField")]
    public string? ValueField { get; set; }

    /// <summary>On button click, send current field text as <c>pluginId</c> (meta override).</summary>
    [JsonPropertyName("metaField")]
    public string? MetaField { get; set; }

    /// <summary>Current value for select nodes.</summary>
    [JsonPropertyName("selected")]
    public string? Selected { get; set; }

    /// <summary>Options for select nodes.</summary>
    [JsonPropertyName("options")]
    public PluginUiOptionDto[]? Options { get; set; }
}

internal sealed class PluginUiOptionDto
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}
