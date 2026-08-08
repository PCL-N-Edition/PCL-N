# 启动器散包更新协议

当前安装包是完全展开的散包：用户启动的是根目录的 C 启动器，AOT 主进程、崩溃处理器、原生库和插件 sidecar 分别位于 `host/`、`crash/`、`native/`、`sidecar/`。更新器必须重建并验证整棵产品文件树，不能只替换一个 EXE。

## 发布端

每个 Release/Beta 的 canonical 包为：

- Windows：`PCL_N_<Configuration>_<RID>_<SelfContained|NoRuntime>.zip`
- Linux/macOS：同名 `.tar.gz`

`generate_patches.py` 解压相邻版本，按相对路径计算 SHA-256 与大小，生成 `patch-index.json`（格式 3）和若干 `*.patch.zip`。补丁 ZIP 内包含：

- `files.json`：源/目标清单哈希、目标文件清单和逐文件操作；
- `patches/*`：比目标文件更小的 HDiffPatch；
- `blobs/*`：新增文件，或差分不划算时的完整替换文件；
- 删除操作不携带载荷。

若整个补丁 ZIP 不小于目标完整包，则不发布该补丁。Action 使用与完整包相同的发布密钥为每个补丁 ZIP、索引和变体清单生成 detached GPG 签名。发布后的完整包、签名、索引和补丁同时上传 GitHub Release 与 R2；正式发布任务缺少 Cloudflare 凭据时会直接失败，避免出现 GitHub 已发布但 R2 未同步的半完成状态。

## 客户端选包与路径规划

1. 用 RID 和 `SelfContained`/`NoRuntime` 匹配 `variants[]`。
2. 从 `https://api.pcln.top/v1/updates/releases/<tag>/patch-index.json` 读取索引；旧客户端仍可忽略格式 3 并下载 GitHub 完整包。
3. 对 `fromVersion -> targetVersion` 边按总下载字节数寻找路径；支持多个历史索引组成多跳链。
4. 没有完整可达路径、任一步校验失败，或补丁总量不小于 `targetArchiveSize` 时，回退完整包。
5. `ci-latest` 永远只提供完整包，不生成补丁。

Cloudflare 更新网关的对象路径固定为：

```text
R2: pcln-releases/releases/<tag>/<asset>
GET https://api.pcln.top/v1/updates/releases/<tag>/<asset>
```

## 重建与验证

对每一步补丁执行：

1. 校验补丁 ZIP 的大小、SHA-256 和 detached GPG 签名；
2. 校验 `files.json` 的版本、路径、重复项、源/目标哈希及载荷信息；
3. 对当前安装树中清单涉及的每个源文件校验大小和 SHA-256，并计算规范清单哈希；
4. 未变化文件复制到新暂存树；`hdiff` 调用 `hpatchz`；`add`/`replace` 解出完整 blob；`delete` 不写目标；
5. 每个输出文件立即校验，Unix 平台同时恢复目标执行位；
6. 重新计算目标清单哈希；
7. 校验最终产品入口的 SHA-256 和 detached GPG 签名。

完整包回退也必须安全解压整棵树，拒绝路径穿越、TAR 符号链接和非普通文件，并执行同样的入口签名校验。macOS 的安装根为 `.app`，入口相对路径是 `Contents/MacOS/PCL-N-Edition`。

## 原子替换

主进程写出本地 `install-plan.json`，再从已校验暂存树启动新的 AOT Host 作为替换帮助程序。帮助程序：

1. 校验计划路径、安装根、暂存根和全部文件；
2. 等待旧 AOT Host 与外层 C 启动器都退出，避免 Windows 可执行文件占用；
3. 将要覆盖/删除的旧文件移动到回滚目录；
4. 逐文件写入 `.pcln-new`、复验后原子改名，产品入口最后替换；
5. 任一步失败则按逆序回滚；成功后按用户选择重启，并由新进程清理工作目录。

仅下载模式只准备暂存树；用户选择“稍后安装”时，在启动器关闭阶段执行替换但不重新启动。

## 兼容策略

- 格式 1/2 的单文件 HDiffPatch 客户端逻辑继续保留，用于老发布包。
- 格式 3 客户端可同时读取旧索引；补丁链不得混用单文件与散包协议。
- Cloudflare 不可用时，网关自身回源 GitHub；补丁索引读取还会直接尝试 GitHub Release。
- 任何不确定状态都回退到带 GPG 签名的完整包，不允许跳过最终校验。
