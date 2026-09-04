namespace PCL.Desktop.Ui;

/// <summary>The original launch-home built-in trivia content, without legacy UI/service code.</summary>
internal static class LaunchWidgetHints
{
    public static IReadOnlyList<string> BuiltIn { get; } = Array.AsReadOnly<string>(
    [
        "今天也要元气满满地启动 Minecraft 哦！",
        "版本设置里可以单独给某个版本指定 Java 和内存。",
        "启动前请确认账户档案已选好，否则会跳转到登录页。",
        "没有本地版本时，点启动会引导你前往下载页安装游戏。",
        "在社区页可以搜索 Mod、整合包、资源包与光影。",
        "下载社区资源不会打断当前页面，任务进度在右下角查看。",
        "游戏崩溃时，可在版本文件夹的 logs/latest.log 查看日志。",
        "实例隔离开启后，Mod 与配置会写在版本文件夹内。",
        "自定义 JVM 参数请谨慎填写，错误参数可能导致无法启动。",
        "正版登录后可在账户页查看与刷新皮肤。",
        "想快速进服？在版本设置的服务器页添加地址后一键启动。",
        "光影需要 Iris / OptiFine 等支持，否则版本页不会显示光影入口。",
        "投影（.litematic 等）需要安装 Litematica 等投影类 Mod。",
        "设置 → 个性化 可以调整主题色与窗口背景。",
        "任务管理页可以取消正在进行的下载与安装任务。",
        "感谢使用 PCL N，也欢迎向社区反馈问题与建议！",
    ]);
}
