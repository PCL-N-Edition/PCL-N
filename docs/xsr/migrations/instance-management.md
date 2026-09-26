# 版本设置与版本管理迁移

行为参考：`dev` 的 `4755faee0a10686917213afe57cb772ade83d77f`，只读检查
`Features/Instances/Views/InstancePageRegistry`、`PageInstanceLeft`、
`InstanceDisplayHelper` 及各右侧页面；不复制旧实现或引用旧程序集。

用户要求版本设置入口承载完整版本管理，不能仅保留游戏/Java 覆盖设置。

安装策略：全局设置 `install.inherit-vanilla` 默认 false，下次安装生效，不允许实例覆盖。
关闭时加载器版本合并原版清单、移除继承与外部 JAR 别名，并持有自己的客户端 JAR；
删除原版版本目录不影响它解析。开启时保留原版父清单依赖。库和 assets 仍由游戏根目录
共享，此策略不同于运行目录隔离。安装开始时快照策略，运行中设置变更不影响本次任务。
已有版本不批量转换；整合包导入的独立 staging 仍使用完整清单。

| 子页 | 必需功能与显示条件 |
| --- | --- |
| 总览 | 图标、分类、名称、描述、收藏、实例目录快捷入口 |
| 启动设置 | 隔离、窗口、Java、内存、服务器与认证、JVM/游戏参数；保留继承与覆盖语义 |
| 组件 | 当前 Minecraft/加载器/附属组件；复用现有版本修改安装流程 |
| 模组 | 存在可安装模组的加载器时显示；搜索、启用/禁用、筛选、排序、多选、更新、删除、本地导入、下载 |
| 资源包 | 搜索、导入、更新、删除、打开目录；明确当前游戏实际目录 |
| 光影包 | 有 OptiFine 或启用的光影支持模组时显示；资源管理操作 |
| 蓝图/投影 | 有启用的对应模组时显示；蓝图资源管理操作 |
| 存档 | 列表、信息、目录操作及旧版支持的管理操作；数据包绑定具体存档 |
| 截图 | 查看和管理实例截图 |
| 服务器 | 管理实例服务器列表 |
| 整合包/导出 | 已安装整合包信息、导出参数和文件选择、打包；复用现有 MRPACK/CurseForge 导入能力 |

旧版的光影匹配候选包括 Iris、Oculus、OptiFine、OptiFabric、Canvas、Angelica；
投影候选包括 Litematica、Schematica、WorldEdit、Axiom、Syncmatica、Baritone。
这些名称是迁移调查输入，不直接照搬旧版文件名子串判断：需识别实际启用的模组 ID、
依赖和加载器组件，区分提供能力的模组与仅有兼容桥/扩展的模组。禁用文件或名称相似
的无关文件不能开启子页。未知能力不能冒充可用；当前子页失去能力时回到总览。

所有路径使用 root-qualified 实例身份及实际隔离/共享 GameDirectory。Service 负责
后台清单扫描、显隐投影、文件操作和任务状态；通过 sealed Query/Command 返回不可变
视图，Desktop 只排版并发意图。不得在渲染或切换标签时同步扫描归档。刷新需取消/代际
隔离，防止上个实例的异步结果覆盖当前页面。文件操作拒绝链接越界与冲突覆盖；运行
中的实例保护、任务取消、失败回滚复用既有机制。大列表保持虚拟化。

已实现首个 sealed query `minecraft.instance.management.query`：后台读取精确实例的现有
安装组件和启用模组元数据，返回管理页目录、游戏版本、隔离/共享内容目录及整合包版本。
Desktop 已接入总览和只读资源目录列表；版本设置将 Java 的 Instance 覆盖设置合入
游戏设置，不再显示独立 Java 或组件选项卡。全局 Java 设置仍独立保留。导航由后台快照决定，离开页面或切换实例取消旧查询；总览作为
失去能力时的回退。目录条目每类最多 10000 项，明确标记截断，跳过链接且不递归；
桌面只创建视口附近的固定高度行，支持滚动到底部与打开当前实际内容目录。
首选 Java 路径接入启动协调器的既有 JavaSelectionService，仍进行兼容性验证；空输入
表示自动选择。未配置 policy 时保留实例原有 Java 选择，实例继承可回到全局策略。

修改版本允许改名，Minecraft 本体版本仍不可变。名称是实际实例目录与清单标识，
不是仅修改 UI 标签。Service 迁移清单/同名 JAR、受管理模组相对路径、其他清单的
inheritsFrom/jar 引用和实例设置键；存档、配置与其他文件随目录移动保留。
拒绝无效名称、目标冲突、链接路径及相关活动游戏进程。失败恢复原清单和目录，设置
最后持久化；备份在回退失败时保留供恢复。改名与组件修改分阶段处理，不能声称跨两者
的全事务回滚。成功后重新扫描并选择新实例。尚未接通的恢复基线不作为改名成功证据。
页目录不等于完整管理能力：模组搜索/更新/删除、导入、存档数据包、截图预览、
服务器编辑和整合包导出仍未完成，不将这些栏目中的只读列表或说明算成功能迁移。
测试覆盖原版、禁用模组、相似 ID、独立 OptiFine、隔离/共享目录、无效实例身份和取消。

模组启停命令使用完整实例目录与单个文件名，通过 Service 将 `.jar` / `.litemod` 与
对应 `.disabled` 文件原子重命名，不覆盖同名目标。提交携带列表中的大小和修改时间，
文件变化后拒绝并要求刷新；拒绝链接路径和非模组文件。进程快照新增实际 GameDirectory，
防止同一共享 mods 目录被另一运行实例使用时仍允许修改；不同根目录同名实例不互相阻塞。
这些检查不宣称抵御同账户恶意进程持续交换文件。完成后 Desktop 重新查询能力与目录。

验收需逐页对照上述 dev 功能，并覆盖：原版、不同加载器、禁用/缺失支持模组、切换实例、
共享目录、文件冲突与取消。接口或空白页面不能算完成，完整管理操作尚待实现。

## Installed content presentation (2026-09-26)

The management query projects local display name, version, description and bounded PNG media.
These fields are presentation metadata, not trusted launch or compatibility facts. Archive reads
use actual-byte limits and a shared query budget; malformed or unsupported metadata falls back
to the filename and a category placeholder. No archive I/O occurs in Desktop rendering.
Content lists expose one details action. Installed-content details retain enable/disable and
trash actions and never navigate to the download catalog. Screenshots use virtualized gallery
rows and a local preview. A stable search toolbar precedes a directory/count line; filtering
changes only realized rows, preserving input identity and focus. Minecraft legacy color and
bold/italic/underline/strikethrough/reset codes are rendered as styled runs without displaying control sequences.

UI.Next text runs carry immutable styled ranges over plain accessible text; the backend applies
them without interpreting Minecraft syntax. Complete-image raster fitting is opt-in so skin-layer
composition is unchanged. Local previews retain at most 1024 pixels per decoded dimension.

### Snapshot recognition of mod enable/disable

Comparison includes both enabled and disabled mod files. An exact-content rename between a
`.jar`/`.litemod` and its `.disabled` counterpart is projected as one enable/disable change,
with both relative paths in the same area. Single-item rollback restores both paths within
the existing validated recovery transaction. Different bytes or an existing counterpart are
ordinary file changes, not guessed toggles. Entering snapshot storage always requests a fresh
comparison, including after launcher settings and content operations.

Resource-pack identity uses the filename without its trailing `.zip` as the primary label.
Both the list and detail title render legacy formatting codes, and search matches the visible
title as well as the original filename; filesystem identities retain the original bytes.
The formatted pack description is secondary (two lines in a list, wrapped in details).
Unrecognized section-sign sequences remain literal text. Content cards keep their painted
surface separate from the padded inner container, so icons and actions have equal insets;
virtual row extents include the inter-card margin and screenshot cards use the same rule.

Text inputs own pointer gestures before ancestor scroll and pager recognizers are armed.
A full click retains input focus after release, and text selection cannot drag the containing
page. Regression coverage includes the real management search click and typing, renderer
press/move/release and cancellation, and native captured selection followed by replacement.

### Mod categories

The local mod list combines text search with All, Enabled, Disabled, Update available,
Package problems and Unchecked categories. Enablement is independent of package health.
Services project nullable package-readability and update facts: missing metadata, exhausted
inspection budgets and unknown online identities never mean corrupt or up to date.
Package inspection covers the archive and supported metadata, not full loader compatibility.
Update checks are explicitly requested, asynchronous and read-only, using Modrinth file hashes
and the instance Minecraft/loader constraints. A candidate must belong to the same project,
be newer than the identified installed release and exclude the installed hash. No file is
uploaded or replaced. Unmatched/offline files remain unknown. Search/category changes retain
the search entity and update only virtualized rows; refresh and detail navigation retain category.
