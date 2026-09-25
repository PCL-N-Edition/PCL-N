# 已安装 Loader 与 Minecraft 兼容性

Services 对实例的实际 Minecraft 本体标识、Loader 种类与版本执行显式目录查询。
目录来自安装器已使用的 IInstallCatalogSource，按游戏与 Loader 分区缓存；GUI 不访问目录或执行判定。
正向结果必须在该游戏版本的目录中找到所选 Loader 的精确版本。缺失/截断的历史目录、离线、
超时或未知 Loader 返回 DependencyMissing，不能把“不在当前目录”解释为已证实不兼容。
原版无需 Loader，返回 true。manifest 中显式 Forge Minecraft 坐标与实际本体不一致时返回 false。
继承链可解析只证明 chain.resolved，不能替代兼容性。查询有时间预算与有界缓存；不会执行安装器。
