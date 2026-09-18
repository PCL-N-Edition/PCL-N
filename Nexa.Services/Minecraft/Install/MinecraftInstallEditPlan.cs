using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public enum MinecraftInstallEditKind { Unchanged, ComponentsOnly, Reinstall }
public sealed record MinecraftInstallEditPlanQuery(MinecraftInstallEditSnapshot Original, IReadOnlyList<InstallBuildSelection> Selection);
public sealed record MinecraftInstallEditPlan(MinecraftInstallEditKind Kind, IReadOnlyList<InstallLoader> ChangedLoaders, string ActionLabel);
public static class MinecraftInstallEditPlanContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.install.edit.plan");
}
public static class MinecraftInstallEditPlanner
{
    public static MinecraftInstallEditPlan Evaluate(MinecraftInstallEditPlanQuery query)
    {
        var before = query.Original.Selection.ToDictionary(item => item.Loader, item => item.Version);
        var after = query.Selection.ToDictionary(item => item.Loader, item => item.Version);
        var changed = before.Keys.Concat(after.Keys).Distinct().Where(loader => before.GetValueOrDefault(loader) != after.GetValueOrDefault(loader)).ToArray();
        bool IsComponent(InstallLoader loader) => InstallCompatibility.IsAddon(loader)
            || loader == InstallLoader.OptiFine && before.Keys.Any(kind => kind != loader && !InstallCompatibility.IsAddon(kind))
                && after.Keys.Any(kind => kind != loader && !InstallCompatibility.IsAddon(kind));
        var kind = changed.Length == 0 ? MinecraftInstallEditKind.Unchanged
            : changed.All(IsComponent) ? MinecraftInstallEditKind.ComponentsOnly : MinecraftInstallEditKind.Reinstall;
        return new(kind, Array.AsReadOnly(changed), kind switch
        {
            MinecraftInstallEditKind.Unchanged => "无需修改",
            MinecraftInstallEditKind.Reinstall => "重新安装",
            _ => changed.All(loader => !before.ContainsKey(loader)) ? "附加安装" : "更新组件",
        });
    }
}
