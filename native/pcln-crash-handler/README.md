# pcln-crash-handler

进程外 C 崩溃监视器：主进程（NativeAOT / 托管）在段错误等场景下无法安全弹 UI，由本程序在父进程消失后兜底写报告并提示用户。

## 与主进程的分工

| 组件 | 职责 |
|------|------|
| `NativeCrashGuard`（进程内） | Windows minidump / Unix 信号笔记 |
| `UnhandledExceptionGuard` | 托管异常 + 会话标记 |
| **`pcln-crash-handler`（本程序）** | 监视父 PID；无 clean-flag 则写报告并弹窗 |

## 协议

```text
pcln-crash-handler \
  --parent-pid <host pid> \
  --marker <CrashSessions/session-….active> \
  --crash-dir <Logs/Crashes> \
  --clean-flag <CrashSessions/session-….clean>
```

- 主进程**正常退出**时写入 `clean-flag`，监视器静默退出。
- 主进程崩溃且未写 clean-flag → 生成 `watchdog-*.md` 并提示用户。

## 构建

```powershell
# Windows
./build.ps1
```

```bash
# Linux / macOS
chmod +x build.sh && ./build.sh
```

将产物命名为：

- Windows: `pcln-crash-handler.exe`
- Unix: `pcln-crash-handler`

## 谁来拉起

| 路径 | 行为 |
|------|------|
| **`pcln-launcher`（推荐）** | launcher 解压 payload 后同时拉起 host + 本程序；host 设 `PCL_SKIP_EXTERNAL_CRASH_HANDLER=1`，经 `PCL_CRASH_CLEAN_FLAG` 写正常退出标记 |
| **无 launcher（开发）** | host 的 `ExternalCrashHandler` 自行探测并拉起（同目录或数据目录 `CrashHandler/`） |

## 发布建议

放入 `payload.zip` 的 `crash/` 目录，由 C launcher 一并解压；或与 host 同目录供开发路径使用。
