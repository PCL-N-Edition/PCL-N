# 启动器更新器

本目录只负责启动器自身更新。资源下载、游戏安装和插件更新不应依赖这里的实现。

## 处理流程

1. `LauncherUpdateService` 根据更新通道检查新版本，并返回一个不可变的 `LauncherUpdateCheckResult`。
2. `LauncherUpdateService.Discovery` 从要求 mTLS 的 Cloudflare 通道端点发现 Release、Beta 或滚动 CI 构建。
3. `LauncherUpdateService.Packages` 选择当前 RID 和运行时变体对应的逻辑更新包；1.4.3 及更新版本必须使用已签名的内容寻址分块图。
4. `LauncherUpdateService.Transport` 统一处理元数据请求、安全重定向和内容长度探测。
5. `LauncherUpdateInstaller` 下载、校验并准备更新；`LauncherBlockUpdateInstaller` 查找本地重复块、下载缺块并重组文件；`LauncherScatterUpdateInstaller` 负责散包清单与替换计划。
6. 安装前由 `LauncherGpgVerifier` 校验发布签名；分块缺失、重建失败或无法安全原地更新时必须保留当前程序并提示手动安装，不得静默回退完整包。

## 发布产物契约

- Windows 更新归档：`PCL_N_<Channel>_<RID>_<Variant>.zip`
- Windows 单文件更新：`PCL_N_<Channel>_<RID>_<Variant>_Portable.exe`，使用独立的 `pcln-blockmap-file-v1` 分块图，重建后仍只替换当前单个可执行文件。
- Linux/macOS 更新归档：`PCL_N_<Channel>_<RID>_<Variant>.tar.gz`
- 归档内容是可直接展开的散包，不能嵌套 ZIP、PDB/DBG 或 Windows 单文件便携版。
- `SelfContained` 与 `NoRuntime` 描述插件 sidecar 是否携带 .NET 运行时；主程序始终是 NativeAOT。
- CI 使用覆盖式 `ci-latest`：每次成功构建用新的签名单文件分块图、`.ci.json` 与 `channels/ci.json` 覆盖上次索引，通过提交 SHA 判断更新；上一轮 CI 独占块立即回收，共享块继续保留，不更新 GitHub Release，也不生成跨版本补丁。散包布局不允许选择或安装 CI 更新。
- 正式发布在流水线中仅把签名分块图、构建元数据和最终程序签名放入 `dist/r2-updates`，将安装包与单文件便携版放入 `dist/downloads`；完整散包归档仅在 runner 上临时用于分块，不进入 R2 或 GitHub Release。
- Beta/Release 为每个散包和 Windows 单文件分别生成 **双分块图**（v1 + v2）及独立 GPG 签名；CI 只为 Windows 单文件提供可安装的滚动更新。原始块使用 SHA-256 内容寻址并保存为 `block/<sha256[0:2]>/<sha256>`；HTTP 路径固定为 `/v1/updates/block/<sha256[0:2]>/<sha256>`。
- 分块算法：
  - **v1** `pcln-fastcdc-v1`：256 KiB / 1 MiB / 2 MiB → `<stem>.blockmap.json`
  - **v2** `pcln-fastcdc-v2`：128 KiB / 512 KiB / 1 MiB → `<stem>.blockmap.v2.json`
- **v1 停发策略**：≤ **1.4.7** 仍双发 v1+v2；**1.4.8 及以后**与 CI 只生成 v2。R2 上已有的 1.4.7 及更早 v1 图/块继续保留复用，不再为新版本重跑 v1 FastCDC。
- R2 保存确定性 gzip 内容（mtime=0, level=9），本地缓存保存通过 SHA-256 校验后的**原始**块。
- 新客户端优先下载 v2 分块图；目标 ≤1.4.7 时若 v2 缺失可回退 v1。目标 ≥1.4.8 不再请求 v1。本地块索引必须使用与清单相同的 FastCDC 算法。
- **LocalBlockIndex（协议 v2）**：安装成功后写入 `{installRoot}/UpdateState/installed.blockmap.json`。下次更新优先按该图的 path/offset 解析源块，避免对整树重新 FastCDC；算法不一致或文件校验失败时回退实时分块。
- **VCDIFF 模型**：blockmap 块条目可带 `full` + `deltas[]`（`vcdiff-rfc3284` + sourceChunks/sourceSha256）。解码失败必须回退 full gzip 块；客户端内置托管解码器，不 `Process.Start` xdelta3。
- **发布**：matrix 各 RID 本地分块 + 批量上传 CAS（`block/` 含 v1/v2 全量块，`delta/` 含 VCDIFF）；中心 job 仅签名 blockmap 并 promote channel。上传走 `upload_r2_cas.py`（S3 ListObjects 跳过 + 并发 PUT；无 S3 密钥时 wrangler 并发回退）。
- 客户端先直接复用未变化文件，再对已安装散包建立本地块索引，只下载仍缺失的块；重组后逐文件校验、校验整树清单并验证最终入口程序的 GPG 签名。
- 1.4.3 是 Cloudflare 分块协议基线。不得为 1.4.3 以前的源版本生成补丁；这类版本只能获取完整包。旧 `patch-index.json` 仅作为过渡兼容，不再由新发布流生成。

## 兼容性约束

- 1.4.3 及更新客户端的更新发现与载荷只允许访问 `api.pcln.top`，Cloudflare/R2 缺失对象必须明确失败，不得回退 GitHub。
- 1.4.3 及更新客户端收到带分块图的更新计划后，签名图或任一块不可用都必须终止该次自动更新，不得请求逻辑包 URL 对应的完整归档。
- 更新身份必须包含散包或单文件布局；两种布局的分块图、入口和签名不得交叉使用。单文件可执行文件即使被用户重命名，也必须以规范入口重建后替换原路径。
- 现有旧版 GitHub 更新资产可保留两周回退窗口，但后续发布不得再写入 GitHub 更新源。
- 分块图、每个原始块、每个重建文件、整树清单和最终重建程序都必须依次通过 GPG/SHA-256 校验后才能进入安装阶段。
- 不要在页面代码中直接创建更新服务或安装器；桌面端由统一协调器管理检查、下载、提示、重启和退出安装。

## 修改原则

- 发布发现、包选择、传输、重建和安装分别修改，避免一个改动同时跨越全部阶段。
- 修改协议或资产命名时，同时更新生成脚本、JSON 上下文、客户端兼容逻辑和定向测试。
- CI 滚动分块与版本补丁是两种独立能力；CI 只发布 `ci-latest` 分块，不保留历史整包或生成版本补丁。
