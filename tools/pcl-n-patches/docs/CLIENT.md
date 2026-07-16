# 启动器接入 Patch

## 查找更新路径

1. 读取启动器当前版本与变体（RID + SelfContained/NoRuntime + WithPlugin/NoPlugin）
2. GET 本仓库最新 Release 中的 `index.json`，或固定 URL：  
   `https://github.com/<owner>/PCL-N-Patches/releases/latest/download/index.json`
3. 在 `variants[]` 中匹配当前变体
4. 在 `patches[]` 中找 `fromVersion == current`（或兼容的规范化版本）
5. 若 `patch.size < targetSize * 0.9`（可配置阈值）→ 下载 patch  
   否则 → 全量下载主仓库 asset

## 应用流程（推荐）

```
current.exe  +  from-to.hdiff  →  hpatchz  →  new.exe.tmp
验证 new.exe.tmp SHA-256 == targetSha256
启动外部脚本：等待进程退出 → 替换 current.exe → 启动 new
```

Windows 替换注意：运行中的 exe 可先改名为 `.old` 再写入新文件。

## 从 PCL-N 触发生成

在 PCL-N 的 release 工作流末尾：

```yaml
- name: Dispatch patch generation
  uses: peter-evans/repository-dispatch@v3
  with:
    token: ${{ secrets.PATCHES_REPO_TOKEN }}
    repository: MuXue1230-owo/PCL-N-Patches
    event-type: pcln-release
    client-payload: |
      {
        "target_tag": "${{ github.event.release.tag_name }}",
        "source_repo": "MuXue1230-owo/PCL-N"
      }
```

`PATCHES_REPO_TOKEN` 需要 `contents: write` 到 PCL-N-Patches。

## 与全量更新的关系

| 模式 | 行为 |
|------|------|
| Patch 命中 | 下载小文件，本地合成目标二进制 |
| Patch 缺失 / 失败 / 不划算 | 回退 GitHub 全量 zip/tar.gz |
| AnnounceOnly | 仅提示，不下载 |
| DownloadAndInstall | patch 或全量后自动替换并重启 |
