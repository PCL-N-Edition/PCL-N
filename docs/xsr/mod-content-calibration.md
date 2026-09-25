# 模组内容与历史校准

候选枚举扩展至 Fabric、Quilt `quilt_loader.jars` 和 Forge/NeoForge
`META-INF/jarjar/metadata.json` 的 path 条目。参照
[Quilt 元数据读取器](https://github.com/QuiltMC/quilt-loader/blob/develop/src/main/java/org/quiltmc/loader/impl/metadata/qmj/V1ModMetadataReader.java)
及 [NeoForge Jar-in-Jar 文档](https://docs.neoforged.net/toolchain/docs/dependencies/jarinjar/)。
这些是声明候选，不声称复现 Loader 的版本选择器或实际加载集合。

顶层和嵌套 JAR 均计算实际内容 SHA-256，不上传路径或 JAR 内容。总哈希读取上限 2 GiB，
单 JAR 256 MiB；嵌套展开继续共用 64 MiB 总预算、16 MiB 单项预算、4 层深度与数量限制。
非法路径、重复 ZIP 条目、缺失子 JAR、超预算或读取期间变化均令清单不完整。
`mod-content-2` 指纹纳入内容摘要和嵌套候选身份，旧版纯元数据指纹不匹配新历史样本。
未知内容摘要、未知版本或不完整依赖不得进行历史校准。

设置指纹扩展为 `game-config-2`：options.txt 加 config/defaultconfigs/scripts/kubejs，
最多 4096 文件、16 层、16 MiB 实际内容。拒绝链接、超限和扫描期间变化；摘要仅用于本地
比较，不上传配置正文。运行中的定期检查和退出检查共用该指纹；退出后再次读取模组内容
指纹，变化或验证失败时不记录校准样本。

Quilt 支持简单必需依赖声明；复合/条件版本表达式仍明确标记不完整，不能假装全部支持。
Forge TOML 依赖解析和实际运行集合/工作负载准入仍需独立完成。
