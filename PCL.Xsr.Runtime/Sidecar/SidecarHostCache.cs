using System.Security.Cryptography;
using PCL.Sidecar.Protocol;

namespace PCL.Xsr.Runtime;

/// <summary>
/// The host-side content cache populated at registration: UI modules and resources arrive
/// inline with their SHA-256 hashes, are verified, stored content-addressed, and are then
/// served from this cache — opening a registered plugin page or reading a resource performs
/// zero IPC.
/// </summary>
public sealed class SidecarHostCache
{
    private readonly Dictionary<XsrSemanticId, byte[]> _uiModules = [];
    private readonly Dictionary<string, byte[]> _resourcesByHash = [];
    private readonly object _gate = new();

    /// <summary>
    /// Gets the number of distinct cached resource blobs (content-addressed by hash).
    /// </summary>
    public int ResourceCount
    {
        get
        {
            lock (_gate)
            {
                return _resourcesByHash.Count;
            }
        }
    }

    /// <summary>
    /// Stores one UI module after verifying its hash. A mismatched hash fails registration.
    /// </summary>
    public void AddUiModule(XsrSemanticId semantic, byte[] payload, byte[] contentHash)
    {
        ArgumentNullException.ThrowIfNull(payload);
        VerifyHash(semantic, payload, contentHash);
        lock (_gate)
        {
            _uiModules[semantic] = payload;
        }
    }

    /// <summary>
    /// Opens a registered UI module from the local cache. This is the zero-IPC path the
    /// renderer uses when the user opens a registered plugin page.
    /// </summary>
    public bool TryOpenUiModule(XsrSemanticId semantic, out byte[]? payload)
    {
        lock (_gate)
        {
            return _uiModules.TryGetValue(semantic, out payload);
        }
    }

    /// <summary>
    /// Stores one resource content-addressed by its verified hash. Transferring the same
    /// content twice stores it once; a mismatched hash fails registration.
    /// </summary>
    public void AddResource(XsrSemanticId semantic, byte[] payload, byte[] contentHash)
    {
        ArgumentNullException.ThrowIfNull(payload);
        VerifyHash(semantic, payload, contentHash);
        string hash = Convert.ToHexString(contentHash);
        lock (_gate)
        {
            if (_resourcesByHash.TryGetValue(hash, out byte[]? existing))
            {
                if (!existing.AsSpan().SequenceEqual(payload))
                {
                    throw new SidecarProtocolException(
                        $"The resource '{semantic}' hashes to an already-cached blob with different content.");
                }

                return;
            }

            _resourcesByHash[hash] = payload;
        }
    }

    /// <summary>
    /// Reads a cached resource by its hash.
    /// </summary>
    public bool TryGetResource(byte[] contentHash, out byte[]? payload)
    {
        string hash = Convert.ToHexString(contentHash);
        lock (_gate)
        {
            return _resourcesByHash.TryGetValue(hash, out payload);
        }
    }

    private static void VerifyHash(XsrSemanticId semantic, byte[] payload, byte[] expectedHash)
    {
        byte[] actual = SHA256.HashData(payload);
        if (!actual.AsSpan().SequenceEqual(expectedHash))
        {
            throw new SidecarProtocolException(
                $"The content of '{semantic}' does not match its declared SHA-256 hash.");
        }
    }
}
