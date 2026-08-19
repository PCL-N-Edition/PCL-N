// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Core.IO.Net;

/// <summary>
/// Runtime toggles for <see cref="PortableHttp"/> (DoH / proxy awareness).
/// Updated from launcher settings; read on each connection.
/// </summary>
public static class PortableNetworkOptions
{
    /// <summary>When true, <see cref="PortableHttp"/> resolves hosts via DNS-over-HTTPS first.</summary>
    public static bool EnableDoH { get; set; } = true;
}
