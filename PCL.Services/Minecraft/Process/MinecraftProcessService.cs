using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Minecraft.Process;

/// <summary>
/// Composition-phase state declaration for the Minecraft process capability.
/// </summary>
public static class MinecraftProcessStateComposition
{
    /// <summary>Ordered collection state key: snapshots keyed by session id.</summary>
    public static readonly XsrSemanticId SessionsKey = XsrSemanticId.Parse("minecraft.process.sessions");

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Collection<MinecraftProcessSnapshot, Guid>(
            SessionsKey,
            "PCL.Services.Minecraft.Process",
            static snapshot => snapshot.SessionId);
    }
}
