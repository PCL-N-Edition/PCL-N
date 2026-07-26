// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using PCL.Desktop.Controls.Legacy;

namespace PCL.Desktop.Features.Community;

internal static class CommunityFavoriteMenu
{
    public static ContextMenu Open(
        Control target,
        CommunityFavoritesStore store,
        CommunityResourceEntry entry,
        CommunityResourceCategory category,
        Action? changed = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ContextMenu menu = Create(store, entry, category, changed);
        target.ContextMenu = menu;
        menu.Open(target);
        return menu;
    }

    internal static ContextMenu Create(
        CommunityFavoritesStore store,
        CommunityResourceEntry entry,
        CommunityResourceCategory category,
        Action? changed = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entry);

        ContextMenu menu = new()
        {
            Placement = PlacementMode.Bottom,
            MinWidth = 180d
        };
        foreach (CommunityFavoriteFolder folder in store.Folders)
        {
            bool contained = store.Contains(entry, folder.Id);
            MenuItem item = new()
            {
                Header = contained
                    ? $"从“{folder.Name}”移除"
                    : $"添加到“{folder.Name}”",
                Tag = folder.Id,
                MinWidth = 170d,
                MinHeight = 32d,
                Padding = new Avalonia.Thickness(14d, 7d),
                Icon = new SvgIcon
                {
                    Icon = contained ? "lucide/star-off" : "lucide/star",
                    Width = 14d,
                    Height = 14d
                }
            };
            string folderId = folder.Id;
            item.Click += (_, args) =>
            {
                args.Handled = true;
                store.Toggle(entry, category, folderId);
                changed?.Invoke();
            };
            menu.Items.Add(item);
        }

        return menu;
    }
}
