# -*- coding: utf-8 -*-
# Weights: define CustomCommand + WaitWindow explicitly.
p = r"PCL.Services\Minecraft\Launch\MinecraftLaunchProgress.cs"
with open(p, encoding="utf-8") as f:
    s = f.read()
old = """    public const double PreLaunchWeight = 1d;
    public const double StartProcessWeight = 2d;
    public const double EndWeight = 1d;

    // The legacy table reserves one weight each for custom_command and wait_window, whose
    // features have not migrated yet. Their weight stays reserved so every migrated stage
    // reports the same overall pacing as the legacy launch.
    public const double Total = 44d;"""
new = """    public const double PreLaunchWeight = 1d;
    public const double StartProcessWeight = 2d;
    public const double WaitWindowWeight = 1d;
    public const double EndWeight = 1d;

    // The legacy table carries one weight for custom_command, whose feature has not migrated;
    // it stays reserved so every migrated stage reports the same overall pacing as the legacy
    // launch. Migrated stages (including wait_window and pre_launch) are consumed for real.
    public const double CustomCommandWeight = 1d;
    public const double Total = 44d;"""
assert s.count(old) == 1, "weights"
s = s.replace(old, new)
with open(p, "w", encoding="utf-8", newline="") as f:
    f.write(s)
print("weights honest")

# Desktop: wait_window display mapping.
p2 = r"PCL.Desktop\Ui\LaunchPageController.cs"
with open(p2, encoding="utf-8") as f:
    s2 = f.read()
old2 = '        ["start_process"] = "启动进程",'
new2 = '        ["start_process"] = "启动进程",\n        ["wait_window"] = "等待游戏窗口",'
assert s2.count(old2) == 1, "label"
s2 = s2.replace(old2, new2)
with open(p2, "w", encoding="utf-8", newline="") as f:
    f.write(s2)
print("wait_window label")

# Tests: the narration order now includes pre_launch.
p3 = r"tests\PCL.Services.Tests\LaunchProgressTests.cs"
with open(p3, encoding="utf-8") as f:
    s3 = f.read()
old3 = '"extract_natives", "start_process", "wait_window", "end",'
assert s3.count(old3) == 1, "order"
s3 = s3.replace(old3, '"extract_natives", "pre_launch", "start_process", "wait_window", "end",')
with open(p3, "w", encoding="utf-8", newline="") as f:
    f.write(s3)
print("test order updated")
