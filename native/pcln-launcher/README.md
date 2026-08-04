# pcln-launcher（C 引导启动器）

用 **C** 写的入口：拉起 **两个进程**（崩溃监视器 + AOT 主机），并指向**已完全展开**的依赖目录。

正式发版 **不在用户机器上解压 zip**：CI 在组装阶段展开 `native/`、`sidecar/`，安装包只复制散文件。

## 发版散包布局（完全展开）

```text
PCL-N-Edition.exe              ← 本程序（用户入口名不变）
host/PCL-N-Host.exe            ← AOT 主机（无内嵌 runtime zip）
crash/pcln-crash-handler.exe
native/                        ← Skia / VLC 等已展开
  libSkiaSharp.dll
  ...
sidecar/                       ← 插件侧车已展开（可选）
  PCL.Plugin.Sidecar.exe
  ...
pcln-layout
```

启动时设置：

| 变量 | 含义 |
|------|------|
| `PCL_LAUNCHER_BOOTSTRAP=1` | 经 C launcher 启动 |
| `PCL_NATIVE_RUNTIME_DIR` | `./native`（就地使用，不再解压） |
| `PCL_PLUGIN_SIDECAR_DIR` / `EXE` | `./sidecar` |
| `PCL_SKIP_EXTERNAL_CRASH_HANDLER=1` | host 不二次拉起 crash-handler |
| `PCL_CRASH_CLEAN_FLAG` | 正常退出标记 |
| `PATH`（Windows） | 含 `native/` |

数据目录仍按 host `LauncherPathLayout` 解析（日志、配置等）；**原生库不再复制到数据目录**。

## 双进程模型

```
pcln-launcher (= PCL-N-Edition)
  ├─ start  host/PCL-N-Host
  └─ start  crash/pcln-crash-handler --parent-pid <host>
  wait(host)
```

## 可选：zip 回退（开发）

若旁路仍有 `native-runtime.zip` / `sidecar.zip`（且无展开目录），launcher 会按内容寻址装入数据目录。**发版产物不得包含 zip。**

## 构建

```powershell
./build.ps1
./../pcln-crash-handler/build.ps1
```

CI：`scripts/assemble-release-layout.sh` 在打包前展开全部依赖。
