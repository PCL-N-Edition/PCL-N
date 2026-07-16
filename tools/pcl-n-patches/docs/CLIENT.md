# 启动器接入 Patch

## 生成策略（服务端）

每个正式/测试发布只生成**有限**直达补丁，避免 Release assets 爆炸：

| 参数 | 默认 | 含义 |
|------|------|------|
| `maxDirectFromVersions` | **10** | 仅最近 10 个版本 → 当前版本 的直达 patch |
| `hopInterval` | **10** | 客户端多跳规划步长参考（如 1→11→21） |

示例（按发布时间排序的稳定版）：

```
… v1 → v2 → v3 → v4 → v5 → v6 → v7(当前)
```

发布 **v7** 时通常只生成窗口直达：

- 最近最多 **10** 个前序版本 → `v7` 的直达 patch

更老客户端走**多跳**（利用**当时**各版本发布时留下的边）：

- 例如 `v1 → v11`（发 v11 时窗口覆盖）再 `v11 → v21` → 路径 `1→11→21`
- 不会为每个远古版本都生成直达当前的 patch

`patch-index.json` / `index.json` 中的 `strategy` 字段描述本规则。

## 查找更新路径

1. 读取启动器当前版本与变体（RID + SelfContained/NoRuntime + WithPlugin/NoPlugin）
2. GET 目标 Release 的 `patch-index.json`（或 `index.json`）
3. 在 `variants[]` 中匹配当前变体
4. **路径搜索**（推荐）：
   - 若 `patches[]` 中有 `fromVersion == current` → 单跳直达
   - 否则：收集**最近若干正式版**的 index（或本地缓存的 hop 图），对边 `from→to` 做 BFS，找 `current → … → target` 的最短链（典型 `1→11→21`）
   - 任一步缺失 / 失败 / patch 不划算 → **全量下载**
5. 若单跳 `patch.size < targetSize * 0.9`（可配置）→ 用 patch，否则全量

## 应用流程（推荐）

```
current.exe  +  from-to.hdiff  →  hpatchz  →  new.exe.tmp
验证 new.exe.tmp SHA-256 == targetSha256
启动外部脚本：等待进程退出 → 替换 current.exe → 启动 new
```

多跳时对链上每一步重复上述流程（中间产物可串成下一步的 `old`）。

Windows 替换注意：运行中的 exe 可先改名为 `.old` 再写入新文件。

## 与全量更新的关系

| 模式 | 行为 |
|------|------|
| 单跳 / 多跳 Patch 成功 | 下载小文件，本地合成目标二进制 |
| 无路径 / 失败 / 不划算 | 回退 GitHub 全量 zip/tar.gz |
| AnnounceOnly | 仅提示，不下载 |
| DownloadAndInstall | patch 或全量后自动替换并重启 |
