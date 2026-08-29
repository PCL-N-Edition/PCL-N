namespace PCL.Sidecar.Protocol;

/// <summary>
/// Reports one deterministic Sidecar protocol failure: framing errors, version mismatches,
/// malformed payloads, and schema violations. Messages never leak beyond the protocol rule.
/// </summary>
public sealed class SidecarProtocolException(string message) : InvalidOperationException(message)
{
}
