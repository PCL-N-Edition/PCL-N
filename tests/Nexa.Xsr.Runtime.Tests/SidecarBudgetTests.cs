using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask SidecarRegistrationRejectsUnboundedInput()
    {
        foreach (string scenario in new[] { "count", "bytes", "identifier", "references", "deadline" })
        {
            var (hostStream, pluginStream) = SidecarLoopbackStream.CreatePair();
            using var host = new SidecarConnection(hostStream);
            using var plugin = new SidecarConnection(pluginStream);
            var first = Item(SidecarRegistrationKind.Command, "plugin.first");
            var second = Item(SidecarRegistrationKind.Command, "plugin.other");
            var limits = new SidecarSessionLimits
            {
                MaximumItems = scenario == "references" ? 2 : 4096,
                MaximumRegistrationBytes = scenario == "bytes" ? SidecarRegistration.EncodeBegin(2).Length + first.Payload.Length + second.Payload.Length - 1 : 1024,
                MaximumSemanticIdCharacters = scenario == "identifier" ? 4 : 256,
                RegistrationTimeout = TimeSpan.FromMilliseconds(200)
            };
            using var session = new SidecarHostSession(host, "Test", limits: limits);
            await CompleteHandshake(session, plugin);
            var pending = session.AcceptRegistrationAsync().AsTask();
            if (scenario != "deadline")
            {
                await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterBegin, SidecarFrameTraits.None,
                    SidecarCorrelationId.Create(), SidecarRegistration.EncodeBegin(scenario == "count" ? uint.MaxValue : 2)));
                if (scenario == "references")
                {
                    byte[] content = "module"u8.ToArray();
                    await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterItem, SidecarFrameTraits.None,
                        SidecarCorrelationId.Create(), SidecarRegistration.EncodeItem(new(SidecarRegistrationKind.UiModule, "plugin.ui", 0, 0,
                            content, System.Security.Cryptography.SHA256.HashData(content), "plugin.r;plugin.r;plugin.r"))));
                    await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterItem, SidecarFrameTraits.None,
                        SidecarCorrelationId.Create(), SidecarRegistration.EncodeItem(new(SidecarRegistrationKind.Resource, "plugin.r", 0, 0,
                            content, System.Security.Cryptography.SHA256.HashData(content)))));
                    await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterEnd, SidecarFrameTraits.Final,
                        SidecarCorrelationId.Create(), Array.Empty<byte>()));
                }
                else if (scenario != "count") await plugin.SendAsync(first);
                if (scenario == "bytes") await plugin.SendAsync(second);
            }
            bool rejected = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (SidecarProtocolException) { rejected = true; }
            AssertTrue(rejected);
            AssertEqual(SidecarSessionState.Failed, session.State);
            AssertTrue(session.Registration is null);
            AssertTrue(session.Mirror is null);
            AssertEqual(0, session.Cache.ResourceCount);
        }
    }

    private static async ValueTask SidecarSnapshotHasIndependentBudget()
    {
        foreach (bool excessiveCount in new[] { false, true })
        {
            var (hostStream, pluginStream) = SidecarLoopbackStream.CreatePair();
            using var host = new SidecarConnection(hostStream);
            using var plugin = new SidecarConnection(pluginStream);
            using var session = new SidecarHostSession(host, "Test", limits: new() { MaximumSnapshotBytes = 20 });
            await CompleteHandshake(session, plugin);
            await RegisterOneState(session, plugin);
            var pending = session.AcceptStateSnapshotAsync().AsTask();
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.StateSnapshotBegin, SidecarFrameTraits.None,
                SidecarCorrelationId.Create(), SidecarStateSnapshot.EncodeBegin(excessiveCount ? uint.MaxValue : 1)));
            if (!excessiveCount) await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.StateSnapshotItem, SidecarFrameTraits.None,
                SidecarCorrelationId.Create(), SidecarStateSnapshot.EncodeItem(1, new byte[32])));
            bool rejected = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (SidecarProtocolException) { rejected = true; }
            AssertTrue(rejected);
            AssertEqual(SidecarSessionState.Failed, session.State);
        }
    }
}
