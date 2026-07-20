// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Features.Community;

/// <summary>Maps community resource categories to game-folder subdirectories.</summary>
internal static class CommunityDownloadPaths
{
    public static string ResolveDirectory(CommunityResourceCategory category, string baseDirectory) =>
        category switch
        {
            CommunityResourceCategory.Mod => Path.Combine(baseDirectory, "mods"),
            CommunityResourceCategory.ResourcePack => Path.Combine(baseDirectory, "resourcepacks"),
            CommunityResourceCategory.Shader => Path.Combine(baseDirectory, "shaderpacks"),
            CommunityResourceCategory.DataPack => Path.Combine(baseDirectory, "datapacks"),
            CommunityResourceCategory.Modpack => Path.Combine(baseDirectory, "modpacks"),
            CommunityResourceCategory.World => Path.Combine(baseDirectory, "saves"),
            _ => baseDirectory
        };
}
