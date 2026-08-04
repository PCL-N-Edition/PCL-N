# pcln-launcher（C 引导启动器）

用 **C** 写的入口：把原先 **AOT 主机内的自解压**搬到这里，并拉起 **两个进程**（崩溃监视器 + AOT 主机）。

## 与 AOT 自解压的对应关系

| 原 AOT（`PclEmbeddedNativeRuntime` / `PclEmbeddedPluginSidecar`） | 现 launcher |
|------------------------------------------------------------------|-------------|
| 内嵌 `NativeRuntime.zip` / `PluginSidecar.zip` | `payload` 内 `native-runtime.zip` / `sidecar.zip` |
| SHA256 内容寻址 | `pcln_sha256_file` → `{hash[:16]}` |
| `{data}/runtime/native/{rid}/{hash}/` | 相同 |
| `.ready` + `.pcln-native-runtime-files` + 安装锁 | 相同 |
| `{data}/runtime/sidecar/{hash}/` + `.extracted` | 相同 |
| 进程内 `Activate` / `NativeLibrary.Load` | **仍在 host**（仅加载） |
| 进程内解压 | **仅当无 `PCL_NATIVE_RUNTIME_DIR` 时的开发回退** |

## 双进程模型

```
pcln-launcher
  ├─ resolve data = host LauncherPathLayout.ResolveDataDirectory
  ├─ extract payload.zip → {data}/runtime/launcher-payload/<hash>/
  ├─ install native-runtime.zip → {data}/runtime/native/<rid>/<sha16>/
  ├─ install sidecar.zip        → {data}/runtime/sidecar/<sha16>/
  ├─ start  host/PCL-N-Host        (进程 A)
  └─ start  crash/pcln-crash-handler --parent-pid <host>  (进程 B)
  wait(host)
```

数据目录与本体 **完全同一套规则**（`LauncherPathLayout`）：

| 项 | 路径 |
|----|------|
| 覆盖文件 | `%LocalAppData%\PCL-N\pcln-paths.json` |
| 默认数据根 | `%AppData%\PCL-N`（Roaming，非 Local） |
| 自定义 | JSON 字段 `ApplicationDataDirectory`（可创建则用之） |
| 崩溃日志 | `{data}\Logs\Crashes`（同 `ResolveLogDirectory`） |

## 环境变量（launcher → host）

| 变量 | 含义 |
|------|------|
| `PCL_LAUNCHER_BOOTSTRAP=1` | 经 C launcher 启动 |
| `PCL_NATIVE_RUNTIME_DIR` | 已安装的 Skia 等目录（host 只 Activate） |
| `PCL_PLUGIN_SIDECAR_DIR` / `PCL_PLUGIN_SIDECAR_EXE` | 已安装侧车 |
| `PCL_SKIP_EXTERNAL_CRASH_HANDLER=1` | 不二次拉起 crash-handler |
| `PCL_CRASH_CLEAN_FLAG` | 正常退出标记 |
| `PCL_DATA_DIRECTORY` | 解析到的数据根 |
| `PATH`（Windows） | 含 native 目录 |

## payload 布局

```text
host/PCL-N-Host.exe
crash/pcln-crash-handler.exe
native-runtime.zip    ← 与 AOT 同源；pack 时转 store(method 0)
sidecar.zip           ← 可选
```

## 构建与打包

```powershell
./build.ps1
./../pcln-crash-handler/build.ps1

# 使用 scripts/build-desktop.ps1 -NativeAot 产出的 runtime zip：
./pack-payload.ps1 `
  -HostExe ..\..\artifacts\win-x64\PCL-N-Edition.exe `
  -NativeRuntimeZip ..\..\artifacts\PCL.NativeRuntime.win-x64.zip `
  -SidecarZip ..\..\artifacts\plugin-sidecar.zip `
  -CrashExe ..\pcln-crash-handler\pcln-crash-handler.exe `
  -Output payload.zip
```

发布目录：

```text
pcln-launcher.exe   # 可改名为产品入口
payload.zip
```

## 与 AOT 内嵌的关系

- **有 launcher + payload**：host 读环境变量，**不再解压**内嵌 zip。
- **无 launcher**（`dotnet run` / 旧单文件）：host 仍可 `EnsureInstalled` 自解压作保底。
- 发布流水线可逐步取消向 AOT 嵌入 runtime/sidecar zip，缩小主机体积。
