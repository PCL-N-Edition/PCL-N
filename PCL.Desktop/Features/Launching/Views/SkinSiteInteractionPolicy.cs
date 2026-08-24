// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Features.Launching.Views;

public static class SkinSiteInteractionPolicy
{
    public static bool CanApplyPublicTexture(LaunchLoginProfileKind profileKind) =>
        profileKind is not LaunchLoginProfileKind.Microsoft and
        not LaunchLoginProfileKind.NCloud;
}
