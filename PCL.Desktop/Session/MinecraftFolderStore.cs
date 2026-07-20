// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using PCL.Application.Settings;
using PCL.Desktop.Features.Instances.Views;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Messaging;

namespace PCL.Desktop.Session;

/// <summary>
/// Single source of truth for Minecraft folder list + selected root (Phase 2).
/// </summary>
public sealed class MinecraftFolderStore
{
    private readonly IMessenger _messenger;
    private readonly List<MinecraftFolderInfo> _folders = [];
    private string? _selectedRoot;
    private bool _loaded;

    public MinecraftFolderStore(IMessenger messenger)
    {
        _messenger = messenger;
    }

    public IReadOnlyList<MinecraftFolderInfo> Folders => _folders;

    public string? SelectedRoot => _selectedRoot;

    public bool IsLoaded => _loaded;

    public void EnsureLoaded(string? preferredRootFromInstance = null)
    {
        // Rebuild every call so env/settings changes (and headless tests that set
        // PCLN_MINECRAFT_ROOTS after the first window) are reflected. Selection is
        // restored from settings / preferred instance / first folder — same as a
        // fresh MainWindow load, not the previous in-memory selection (which may
        // predate env root injection).
        _folders.Clear();
        _loaded = true;

        foreach (string root in LaunchInstanceDiscovery.GetCandidateRoots())
        {
            string? normalized = SessionPath.NormalizeDirectory(root);
            if (normalized is null || ContainsRoot(normalized))
                continue;

            _folders.Add(new MinecraftFolderInfo(
                LaunchInstanceDiscovery.GetMinecraftRootDisplayName(normalized),
                normalized));
        }

        string? settingsSelected = null;
        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            string serializedFolders = settings.GetTextOption(LauncherSettingKeys.LaunchMinecraftFolders);
            if (!string.IsNullOrWhiteSpace(serializedFolders))
            {
                foreach (MinecraftFolderSetting custom in ParseFolderSettings(serializedFolders))
                {
                    string? normalized = SessionPath.NormalizeDirectory(custom.RootDirectory);
                    if (normalized is null || ContainsRoot(normalized))
                        continue;

                    string name = string.IsNullOrWhiteSpace(custom.Name)
                        ? LaunchInstanceDiscovery.GetMinecraftRootDisplayName(normalized)
                        : custom.Name.Trim();
                    _folders.Add(new MinecraftFolderInfo(name, normalized, IsCustom: true));
                }
            }

            settingsSelected = SessionPath.NormalizeDirectory(
                settings.GetTextOption(LauncherSettingKeys.LaunchSelectedMinecraftRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or JsonException)
        {
            settingsSelected = null;
        }

        if (_folders.Count == 0)
        {
            string fallback = LaunchInstanceDiscovery.GetCurrentMinecraftRoot();
            _folders.Add(new MinecraftFolderInfo(
                "当前文件夹",
                SessionPath.NormalizeDirectory(fallback) ?? fallback));
        }

        string? fromInstance = SessionPath.TryGetMinecraftRootFromInstanceDirectory(preferredRootFromInstance);
        if (settingsSelected is not null && ContainsRoot(settingsSelected))
            _selectedRoot = settingsSelected;
        else if (fromInstance is not null && ContainsRoot(fromInstance))
            _selectedRoot = fromInstance;
        else
            _selectedRoot = _folders[0].RootDirectory;
    }

    public bool TrySetSelectedRoot(string? rootDirectory)
    {
        string? normalized = SessionPath.NormalizeDirectory(rootDirectory);
        if (normalized is null || !ContainsRoot(normalized))
            return false;

        if (string.Equals(_selectedRoot, normalized, StringComparison.OrdinalIgnoreCase))
            return false;

        _selectedRoot = normalized;
        Persist();
        _messenger.Send(new FolderSelectionChangedMessage(_selectedRoot));
        return true;
    }

    public void SetSelectedRootWithoutPersist(string? rootDirectory)
    {
        string? normalized = SessionPath.NormalizeDirectory(rootDirectory) ?? rootDirectory;
        if (normalized is not null && ContainsRoot(normalized))
            _selectedRoot = normalized;
    }

    public MinecraftFolderInfo AddOrGet(string rootDirectory, string name, bool isCustom = true)
    {
        string normalized = SessionPath.NormalizeDirectory(rootDirectory)
                            ?? throw new ArgumentException("Minecraft 文件夹路径无效。", nameof(rootDirectory));
        MinecraftFolderInfo? existing = _folders.FirstOrDefault(folder =>
            string.Equals(folder.RootDirectory, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        MinecraftFolderInfo added = new(name, normalized, IsCustom: isCustom);
        _folders.Add(added);
        Persist();
        return added;
    }

    public bool TryRename(MinecraftFolderInfo folder, string? name)
    {
        if (!folder.IsCustom || string.IsNullOrWhiteSpace(name))
            return false;

        int index = _folders.IndexOf(folder);
        if (index < 0)
        {
            string? path = SessionPath.NormalizeDirectory(folder.RootDirectory);
            if (path is null)
                return false;
            index = _folders.FindIndex(candidate =>
                string.Equals(
                    SessionPath.NormalizeDirectory(candidate.RootDirectory),
                    path,
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0 || !_folders[index].IsCustom)
                return false;
        }

        _folders[index] = _folders[index] with { Name = name.Trim(), IsCustom = true };
        Persist();
        return true;
    }

    /// <summary>
    /// Removes a folder. When the selected root is removed, returns the folder that should become selected.
    /// </summary>
    public MinecraftFolderInfo? Remove(MinecraftFolderInfo folder)
    {
        string? removedPath = SessionPath.NormalizeDirectory(folder.RootDirectory);
        int index = _folders.IndexOf(folder);
        if (index < 0 && removedPath is not null)
        {
            index = _folders.FindIndex(candidate =>
                string.Equals(
                    SessionPath.NormalizeDirectory(candidate.RootDirectory),
                    removedPath,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (index < 0)
            return null;

        _folders.RemoveAt(index);

        if (_folders.Count == 0)
        {
            string fallback = SessionPath.NormalizeDirectory(LaunchInstanceDiscovery.GetCurrentMinecraftRoot())
                              ?? LaunchInstanceDiscovery.GetCurrentMinecraftRoot();
            _folders.Add(new MinecraftFolderInfo("当前文件夹", fallback));
        }

        bool removedSelected = removedPath is not null &&
            string.Equals(
                SessionPath.NormalizeDirectory(_selectedRoot),
                removedPath,
                StringComparison.OrdinalIgnoreCase);

        if (removedSelected)
        {
            _selectedRoot = _folders[0].RootDirectory;
            Persist();
            _messenger.Send(new FolderSelectionChangedMessage(_selectedRoot));
            return _folders[0];
        }

        Persist();
        return null;
    }

    public void Persist()
    {
        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            MinecraftFolderSetting[] customFolders = _folders
                .Where(static folder => folder.IsCustom)
                .Select(static folder => new MinecraftFolderSetting(folder.Name, folder.RootDirectory))
                .ToArray();
            settings.SetTextOption(
                LauncherSettingKeys.LaunchMinecraftFolders,
                SerializeFolderSettings(customFolders));
            settings.SetTextOption(
                LauncherSettingKeys.LaunchSelectedMinecraftRoot,
                _selectedRoot ?? string.Empty);
            LauncherSettingsPageBinder.SaveSettings(settings, notify: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Callers may surface via UI log; store stays in memory.
            System.Diagnostics.Debug.WriteLine("MinecraftFolderStore.Persist failed: " + ex.Message);
        }
    }

    public bool ContainsRoot(string rootDirectory) =>
        _folders.Any(folder =>
            string.Equals(folder.RootDirectory, rootDirectory, StringComparison.OrdinalIgnoreCase));

    private static MinecraftFolderSetting[] ParseFolderSettings(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        List<MinecraftFolderSetting> result = [];
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            string? name = TryReadJsonString(element, "name") ?? TryReadJsonString(element, "Name");
            string? rootDirectory = TryReadJsonString(element, "rootDirectory") ??
                                    TryReadJsonString(element, "RootDirectory");
            if (!string.IsNullOrWhiteSpace(rootDirectory))
                result.Add(new MinecraftFolderSetting(name ?? string.Empty, rootDirectory));
        }

        return result.ToArray();
    }

    private static string SerializeFolderSettings(IEnumerable<MinecraftFolderSetting> folders)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            foreach (MinecraftFolderSetting folder in folders)
            {
                writer.WriteStartObject();
                writer.WriteString("name", folder.Name);
                writer.WriteString("rootDirectory", folder.RootDirectory);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? TryReadJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record MinecraftFolderSetting(string Name, string RootDirectory);
}
