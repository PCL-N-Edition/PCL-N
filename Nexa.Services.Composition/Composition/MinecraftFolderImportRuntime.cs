using System.Text.Json;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public sealed record MinecraftFolderImportRuntime(XsrCommandRouter Commands, XsrQueryRouter Queries);

public static class MinecraftFolderImportRuntimeComposer
{
    public static MinecraftFolderImportRuntime Compose(FoundationHost host, MinecraftLibraryService library, IXsrDispatchObserver observer)
    {
        var service = new MinecraftFolderImportService(host.Tasks);
        XsrCommandRouterBuilder commands = new();
        commands.Register<MinecraftFolderImportCommand>(MinecraftFolderImportContract.Import, async (command, token) =>
        {
            var result = await service.ImportAsync(command, token).ConfigureAwait(false);
            if (result.IsSuccess) await library.RefreshAsync(token).ConfigureAwait(false);
            return result;
        });
        XsrQueryRouterBuilder queries = new();
        queries.Register<MinecraftFolderInspectQuery, MinecraftFolderInspection>(MinecraftFolderImportContract.Inspect, async (query, token) =>
        {
            try { return XsrResult.Success(await MinecraftFolderImportService.InspectAsync(query.Path, token).ConfigureAwait(false)); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
            { return XsrResult.Failure<MinecraftFolderInspection>(MinecraftErrors.InvalidRequest(error.Message)); }
        });
        return new(commands.Build(observer), queries.Build(observer));
    }
}
