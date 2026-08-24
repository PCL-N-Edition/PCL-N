// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Localization;
using PCL.Desktop.Messaging;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Host renderer for a sidecar-owned settings-style root page. The sidecar owns
/// the groups, item order, labels and child page ids; Desktop only supplies the
/// cross-platform left/right shell.
/// </summary>
internal static class PluginRemoteNavigationPage
{
    public static DesktopMainPage Create(PluginUiPageDto descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        PluginUiNavigationGroupDto[] groups = descriptor.NavigationGroups?
            .OrderBy(static group => group.Order)
            .Select(static group => new PluginUiNavigationGroupDto
            {
                Title = group.Title,
                TitleKey = group.TitleKey,
                Order = group.Order,
                Items = group.Items
                    .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
                    .OrderBy(static item => item.Order)
                    .ToArray()
            })
            .Where(static group => group.Items.Length > 0)
            .ToArray() ?? [];

        PluginUiNavigationItemDto? first = groups.SelectMany(static group => group.Items).FirstOrDefault();
        if (first is null)
            return new DesktopMainPage(null, new PageSetupRemoteDataChain(descriptor.Id));

        RemotePageHost right = new(first.Id);
        RemoteNavigationLeft left = new(groups, right.SwitchTo);
        return new DesktopMainPage(
            left,
            right,
            Activated: () =>
            {
                left.TriggerShowAnimation();
                right.Activate();
            });
    }

    private sealed class RemoteNavigationLeft : MyPageLeft
    {
        private readonly Action<string> selectPage;
        private readonly Dictionary<string, MyListItem> items = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(TextBlock Label, PluginUiNavigationGroupDto Group)> groupLabels = [];
        private readonly PluginUiNavigationGroupDto[] groups;
        private string? selectedId;
        private bool localizationAttached;

        public RemoteNavigationLeft(
            PluginUiNavigationGroupDto[] groups,
            Action<string> selectPage)
        {
            this.groups = groups;
            this.selectPage = selectPage;
            Width = 152;

            StackPanel panel = new()
            {
                Name = "PanItem",
                Margin = new Thickness(0, 12, 0, 0)
            };
            foreach (PluginUiNavigationGroupDto group in groups)
            {
                TextBlock label = new()
                {
                    Margin = new Thickness(13, 5, 5, 3),
                    Opacity = 0.55,
                    FontSize = 12,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                };
                panel.Children.Add(label);
                groupLabels.Add((label, group));

                foreach (PluginUiNavigationItemDto descriptor in group.Items)
                {
                    MyListItem item = new()
                    {
                        Height = 36,
                        VerticalAlignment = VerticalAlignment.Top,
                        MinPaddingRight = 35,
                        IsScaleAnimationEnabled = false,
                        Type = MyListItem.CheckType.RadioBox,
                        Tag = descriptor.Id,
                        LogoScale = 0.95,
                        SvgIcon = string.IsNullOrWhiteSpace(descriptor.Icon)
                            ? "lucide/circle"
                            : descriptor.Icon
                    };
                    item.Click += (_, _) => Select(descriptor.Id, notify: true);
                    panel.Children.Add(item);
                    items.Add(descriptor.Id, item);
                }
            }

            Children.Add(new MyScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel
            });
            AnimatedControl = panel;
            RefreshText();
            Select(groups.SelectMany(static group => group.Items).First().Id, notify: false);

            AttachedToVisualTree += (_, _) => AttachLocalization();
            DetachedFromVisualTree += (_, _) => DetachLocalization();
        }

        private void Select(string pageId, bool notify)
        {
            if (string.Equals(selectedId, pageId, StringComparison.OrdinalIgnoreCase))
            {
                SyncChecks();
                return;
            }

            selectedId = pageId;
            SyncChecks();
            if (notify)
                selectPage(pageId);
        }

        private void SyncChecks()
        {
            foreach ((string id, MyListItem item) in items)
            {
                item.SetChecked(
                    string.Equals(id, selectedId, StringComparison.OrdinalIgnoreCase),
                    user: false,
                    animate: false);
            }
        }

        private void RefreshText()
        {
            foreach ((TextBlock label, PluginUiNavigationGroupDto group) in groupLabels)
                label.Text = Resolve(group.TitleKey, group.Title);

            foreach (PluginUiNavigationItemDto descriptor in groups.SelectMany(static group => group.Items))
            {
                if (items.TryGetValue(descriptor.Id, out MyListItem? item))
                    item.Title = Resolve(descriptor.TitleKey, descriptor.Title);
            }
        }

        private void AttachLocalization()
        {
            if (localizationAttached)
                return;
            localizationAttached = true;
            AvaloniaLocalizationManager.LanguageChanged += LanguageChanged;
        }

        private void DetachLocalization()
        {
            if (!localizationAttached)
                return;
            localizationAttached = false;
            AvaloniaLocalizationManager.LanguageChanged -= LanguageChanged;
        }

        private void LanguageChanged(object? sender, EventArgs eventArgs) =>
            Dispatcher.UIThread.Post(RefreshText);
    }

    private sealed class RemotePageHost : ContentControl
    {
        private const string NetworkServersPageId = "pcl.plugin.network.servers";
        private readonly Dictionary<string, MyPageRight> pages =
            new(StringComparer.OrdinalIgnoreCase);
        private MyPageRight current;
        private bool active;

        public RemotePageHost(string initialPageId)
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            current = GetOrCreate(initialPageId);
            Content = current;
            DetachedFromVisualTree += (_, _) => Deactivate();
        }

        public void Activate()
        {
            if (active)
                return;
            active = true;
            current.PageOnEnter();
        }

        public void SwitchTo(string pageId)
        {
            MyPageRight next = GetOrCreate(pageId);
            if (ReferenceEquals(next, current))
                return;

            if (active)
                current.PageOnExit();
            current = next;
            Content = current;
            if (active)
                current.PageOnEnter();
        }

        private MyPageRight GetOrCreate(string pageId)
        {
            if (!pages.TryGetValue(pageId, out MyPageRight? page))
            {
                page = string.Equals(pageId, NetworkServersPageId, StringComparison.OrdinalIgnoreCase)
                    ? CreateServerCatalogPage()
                    : new PageSetupRemoteDataChain(pageId);
                pages.Add(pageId, page);
            }

            return page;
        }

        private static PageCommunityRight CreateServerCatalogPage()
        {
            PageCommunityRight page = new(
                new ModrinthServerCommunityCatalog(),
                ownsCatalog: true,
                initialCategory: CommunityResourceCategory.Server);
            page.JoinServerRequested += (_, entry) =>
                WeakReferenceMessenger.Default.Send(new CommunityServerJoinRequestedMessage(entry));
            return page;
        }

        private void Deactivate()
        {
            if (!active)
                return;
            active = false;
            current.PageOnExit();
        }
    }

    private static string Resolve(string? key, string fallback) =>
        string.IsNullOrWhiteSpace(key)
            ? fallback
            : AvaloniaLocalizationManager.GetText(key, fallback);
}

/// <summary>Keeps localized sidecar root-page titles available to the shell.</summary>
internal static class PluginSidecarNavigationText
{
    private static readonly Dictionary<string, (string? Key, string Fallback)> Entries =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static void Register(string route, string? key, string fallback)
    {
        lock (Gate)
            Entries[route] = (key, fallback);
    }

    public static void Remove(string route)
    {
        lock (Gate)
            Entries.Remove(route);
    }

    public static string Resolve(string route, string fallback)
    {
        (string? Key, string Fallback) entry;
        lock (Gate)
        {
            if (!Entries.TryGetValue(route, out entry))
                return fallback;
        }

        return string.IsNullOrWhiteSpace(entry.Key)
            ? entry.Fallback
            : AvaloniaLocalizationManager.GetText(entry.Key, entry.Fallback);
    }
}
