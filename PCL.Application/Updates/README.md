# 启动器更新器

本目录只负责启动器自身更新。资源下载、游戏安装和插件更新不应依赖这里的实现。

## 处理流程

1. `LauncherUpdateService` 根据更新通道检查新版本，并返回一个不可变的 `LauncherUpdateCheckResult`。
2. `LauncherUpdateService.Discovery` 从 GitHub Atom/发布页与构建元数据发现 Release、Beta 或滚动 CI 构建。
3. `LauncherUpdateService.Packages` 选择当前 RID 和运行时变体对应的完整包，并在兼容且确实更小时规划补丁链。
4. `LauncherUpdateService.Transport` 统一处理元数据请求、安全重定向和内容长度探测。
5. `LauncherUpdateInstaller` 下载、校验并准备更新；`LauncherScatterUpdateInstaller` 负责散包清单、逐文件重建与替换。
6. 安装前由 `LauncherGpgVerifier` 校验发布签名；失败或无法安全原地更新时必须保留当前程序并回退到完整包或手动安装提示。

## 发布产物契约

- Windows 更新归档：`PCL_N_<Channel>_<RID>_<Variant>.zip`
- Linux/macOS 更新归档：`PCL_N_<Channel>_<RID>_<Variant>.tar.gz`
- 归档内容是可直接展开的散包，不能嵌套 ZIP、PDB/DBG 或 Windows 单文件便携版。
- `SelfContained` 与 `NoRuntime` 描述插件 sidecar 是否携带 .NET 运行时；主程序始终是 NativeAOT。
- CI 使用覆盖式 `ci-latest`：每次成功构建上传完整更新归档、签名和 `.ci.json`，通过提交 SHA 判断更新，不生成跨版本补丁。
- 正式发布在流水线中将散包更新文件放入 `dist/updates`，将安装包与单文件便携版放入 `dist/downloads`；GitHub 同时分发两类文件，Cloudflare R2 只保存更新器需要的前一类。
- Beta/Release 可以附带 `patch-index.json`。缺少索引、布局不兼容、变体不匹配、校验信息无效或补丁不划算时，客户端必须自动使用完整包。

## 兼容性约束

- 保留旧版资产命名和 GitHub Release 回退地址，避免已发布客户端失去更新入口。
- 新版优先从 `api.pcln.top` 获取更新文件；发布发现仍使用不消耗 GitHub REST API 限额的 Atom/HTML 表面。
- 更新包、补丁和最终重建程序都必须通过大小、SHA-256、文件清单与 GPG 校验后才能进入安装阶段。
- 不要在页面代码中直接创建更新服务或安装器；桌面端由统一协调器管理检查、下载、提示、重启和退出安装。

## 修改原则

- 发布发现、包选择、传输、重建和安装分别修改，避免一个改动同时跨越全部阶段。
- 修改协议或资产命名时，同时更新生成脚本、JSON 上下文、客户端兼容逻辑和定向测试。
- CI 更新归档与版本补丁是两种独立能力；禁用 CI 补丁不能移除 CI 完整更新归档。
