# PCL.N Update Protocol v2

## 1. 最终数据路径

```text
Build output
    │
    ▼
File scanner
    │
    ├─ SHA-256 file
    │
    ▼
FastCDC v2
128 KiB / 512 KiB / 1 MiB
    │
    ├──────────── SHA256 exact hit ───────────► CAS reuse
    │
    ▼
changed/new chunk
    │
    ▼
Previous-version source window
    │
    ▼
VCDIFF
    │
    ├─ delta worthwhile ─────────────────────► delta/
    │
    └─ delta not worthwhile ─────────────────► full block only
    │
    ▼
blockmap v2
    │
    ▼
parallel R2 upload
    │
    ▼
manifest + signature
    │
    ▼
atomic channel promotion
```

客户端：

```text
Target chunk
   │
   ├─ 已在本地存在 ───────────────► 直接使用
   │
   ├─ 可使用 VCDIFF ─────────────► source + delta
   │
   └─ 否则 ──────────────────────► 下载 full block
```

任何 VCDIFF 错误都不能导致更新失败：

```text
delta failure
     ↓
full block fallback
```

---

# 2. FastCDC v2 参数

最终默认值：

```text
algorithm = pcln-fastcdc-v2

min = 128 KiB
avg = 512 KiB
max = 1 MiB
```

不要再使用现在接近 2 MiB 的大 chunk 作为主要粒度。

SHA-256 始终基于：

```text
raw uncompressed chunk
```

而不是 gzip 后的数据。

CAS identity：

```text
SHA256(raw)
```

因此即使以后更换：

```text
gzip
  ↓
zstd
```

也不改变 chunk identity。

---

# 3. 不采用简单的“1 target chunk → 1 old chunk”

最终版建议直接做 **VCDIFF Source Window**。

原因是 FastCDC 边界在新旧版本间可能略微移动。

例如旧版本：

```text
|----- A -----|----- B -----|----- C -----|
```

新版本：

```text
      |---------- X ----------|
```

如果只拿 `B` 给 VCDIFF：

```text
B -> X
```

会损失 A/C 中本来可以 COPY 的数据。

因此使用：

```text
A + B + C
    ↓
source window
    ↓
VCDIFF
    ↓
X
```

推荐：

```text
Target chunk avg       512 KiB

Source window:
    corresponding old chunk
    + previous chunk
    + next chunk

Typical source:
    ~1–3 MiB
```

特殊情况下扩到：

```text
±2 chunks
```

上限：

```text
4 MiB
```

这样既提高 delta quality，又不会让 xdelta3 每次拿整个 200 MiB executable 做搜索。

---

# 4. Source Window 标识

不要绑定：

```text
v1.4.3 -> v1.4.4
```

这种版本号 patch。

用 source chunk sequence：

```json
{
  "bases": [
    "sha256-A",
    "sha256-B",
    "sha256-C"
  ]
}
```

拼接：

```text
RAW(A) || RAW(B) || RAW(C)
```

然后计算：

```text
sourceWindowSha256
```

最终 descriptor：

```json
{
  "algorithm": "vcdiff-rfc3284",
  "source": {
    "chunks": [
      "sha256-A",
      "sha256-B",
      "sha256-C"
    ],
    "sha256": "SOURCE_WINDOW_SHA256",
    "size": 1843291
  },
  "path": "delta/v2/ab/<target>/<source>.vcdiff",
  "size": 28493
}
```

这样它仍然是 **CAS-oriented delta**，不是传统版本 patch。

---

# 5. blockmap v2

建议：

```json
{
  "formatVersion": 2,
  "layout": "pcln-blockmap-file-v2",
  "algorithm": "pcln-fastcdc-v2",

  "chunking": {
    "min": 131072,
    "avg": 524288,
    "max": 1048576
  },

  "compression": "gzip",

  "targetFiles": [
    {
      "path": "PCL-N-Edition.exe",
      "sha256": "...",
      "size": 123899904,

      "chunks": [
        {
          "sha256": "TARGET",
          "size": 612338,

          "full": {
            "path": "block/ab/TARGET",
            "compressedSize": 218443
          },

          "deltas": [
            {
              "algorithm": "vcdiff-rfc3284",
              "sourceChunks": [
                "OLD1",
                "OLD2",
                "OLD3"
              ],
              "sourceSha256": "...",
              "path": "delta/v2/ab/TARGET/SOURCE.vcdiff",
              "size": 19482
            }
          ]
        }
      ]
    }
  ]
}
```

---

# 6. Delta 接受规则

最终规则：

```text
fullSize = compressed full block size
deltaSize = VCDIFF size

接受 delta 当：

deltaSize <= fullSize * 0.70
AND
fullSize - deltaSize >= 16 KiB
```

如果：

```text
full = 600 KiB
delta = 430 KiB
```

不存。

如果：

```text
full = 600 KiB
delta = 80 KiB
```

存。

第一版每个 target：

```text
最多 2 个 delta representations
```

而不是 1 个。

优先：

```text
N-1 release
N-2 release
```

但如果两个历史版本产生相同 source-window hash，则自动合并成一个。

---

# 7. VCDIFF

采用 xdelta3 / VCDIFF RFC 3284。

xdelta3 当前就是 VCDIFF/RFC 3284 的 C 实现，并同时提供库和 CLI；因此协议层只定义 `vcdiff-rfc3284`，不要写死具体 encoder 实现。([GitHub][2])

构建端：

```text
xdelta3 encoder
```

客户端：

```text
native xdelta3 decoder
      │
      ▼
static P/Invoke
```

支持：

```text
win-x64
win-arm64
linux-x64
linux-arm64
osx-x64
osx-arm64
```

不要：

```text
Process.Start("xdelta3")
```

正式客户端直接 native interop。

---

# 8. 客户端完整性模型

严格顺序：

```text
load local base chunks
        ↓
verify SHA256(each chunk)
        ↓
concatenate source window
        ↓
verify sourceWindowSha256
        ↓
VCDIFF decode
        ↓
verify SHA256(target)
        ↓
commit
```

任何一步失败：

```text
discard
   ↓
download full block
```

因此 VCDIFF 永远只是：

> **优化路径**

而不是可用性依赖。

---

# 9. LocalBlockIndex

客户端安装成功后保存：

```text
UpdateState/
    installed.blockmap.json
```

不用真正缓存一份完整 block。

通过 blockmap：

```text
chunk SHA
    ↓
file path
offset
length
```

就能从当前安装文件中直接读取 source。

例如：

```text
PCL-N-Edition.exe

block A
offset = 31298341
size   = 428128
```

所以不会因为 v2 引入额外几百 MiB block cache。

---

# 10. 发布流程必须改成 Pipeline

这是发布速度优化的重点。

旧模型不要再是：

```text
chunk
 ↓
hash
 ↓
HEAD R2
 ↓
compress
 ↓
upload
 ↓
下一个 chunk
```

必须变成：

```text
                 ┌─ hash workers
                 │
File scanner ────┼─ FastCDC workers
                 │
                 ├─ compression workers
                 │
                 ├─ VCDIFF workers
                 │
                 └─ upload workers
```

阶段之间：

```text
bounded Channel<T>
```

或者：

```text
System.Threading.Channels
```

整个 pipeline 持续有工作。

---

# 11. 一个文件只扫描一次

不要分别为了：

```text
SHA256
FastCDC
compression
delta candidate
```

把目标文件读四遍。

目标：

```text
Sequential read
     │
     ├─ file SHA256
     ├─ FastCDC rolling state
     └─ chunk buffers
```

chunk cut 后才进入后台：

```text
hash
compress
delta
```

对于大文件优先：

```text
FileStream
Options = SequentialScan
large buffer
async
```

减少重复磁盘 I/O。

---

# 12. Chunk buffer 使用池

禁止：

```csharp
new byte[512 * 1024]
```

几千次。

使用：

```text
ArrayPool<byte>
MemoryPool<byte>
```

或者自己的：

```text
1 MiB slab pool
```

Pipeline：

```text
disk
 ↓
pooled memory
 ↓
FastCDC
 ↓
hash/compress/delta
 ↓
upload
 ↓
return buffer
```

避免 LOH 压力和 GC pause。

---

# 13. Hash / gzip / VCDIFF 分线程池

不要所有任务都扔：

```text
Task.Run()
```

建议明确三类 worker：

```text
CDC/hash
    CPU count

compression
    CPU count / 2

VCDIFF
    CPU count / 2

network upload
    16~32
```

再由 bounded queue 做 backpressure。

例如 8 核 runner：

```text
scanner       1
CDC/hash      4
gzip          4
VCDIFF        4
R2 PUT       24
```

实际值用 benchmark 自动选择。

---

# 14. Delta 要晚于 Exact CAS 判断

顺序必须是：

```text
FastCDC
  ↓
SHA256
  ↓
CAS exact hit?
  │
 YES ─────────► done
  │
 NO
  ▼
gzip full
  │
  ├────────────► upload candidate
  │
  ▼
VCDIFF candidates
```

如果 CAS 已经有 target：

```text
不要 gzip
不要 VCDIFF
不要上传
```

这是发布端最重要的 CPU shortcut 之一。

---

# 15. R2 发布：从 Wrangler/REST 切换到 S3 API

发布程序不要执行几千次：

```text
npx wrangler r2 object put ...
```

也不要通过 Cloudflare REST API 做高频数据上传。

Cloudflare 明确把 S3-compatible API / Workers API 作为高吞吐对象操作接口；REST API 有账户级请求限制。([Cloudflare Docs][3])

发布器直接使用：

```text
AWS SDK for .NET
        +
R2 S3 endpoint
```

例如：

```text
AmazonS3Client
```

endpoint：

```text
https://<ACCOUNT>.r2.cloudflarestorage.com
```

R2 官方支持 S3-compatible API。([Cloudflare Docs][4])

---

# 16. 不要对这些 block 使用 Multipart

你新的物理 block：

```text
<= 1 MiB raw
```

压缩后通常更小。

R2 multipart 最小普通 part 为 **5 MiB**；它主要适合大型对象和需要并行 part/resume 的场景。因此这些 CAS block 应直接使用普通 `PutObject`。([Cloudflare Docs][5])

也就是：

```text
24 × concurrent PutObject
```

而不是：

```text
multipart
```

---

# 17. R2 上传并发

推荐初始：

```text
MinConcurrency = 8
Default        = 24
Max            = 48
```

自动调节：

```text
latency low + no 429
    ↓
increase concurrency

429 / timeout
    ↓
decrease concurrency
```

使用：

```text
SemaphoreSlim
```

或 Channel worker pool。

R2 对同一个 object key 的并发写有限制，所以同一进程首先按 SHA 去重，保证同一个 key 在本次发布中只有一个 producer。([Cloudflare Docs][6])

---

# 18. 禁止逐 block HEAD

最差做法：

```text
3594 blocks
 ×
HEAD
```

然后再 PUT。

改成：

```text
startup
   ↓
ListObjectsV2("block/")
   ↓
HashSet<SHA256>
```

当前只有：

```text
3594 objects
```

几次分页就能构建完整存在性索引。

R2 S3 API 支持 `ListObjectsV2`、prefix 和 continuation token。([Cloudflare Docs][7])

然后本地：

```csharp
if (remoteBlocks.Contains(hash))
    skip;
```

无需网络 RTT。

---

# 19. 同时使用 Conditional Put

即使本地 inventory 判断不存在，也存在多个 CI job 的 race。

因此：

```http
If-None-Match: *
```

进行 immutable CAS create。

R2 的 `PutObject` 支持 `If-None-Match` 等 conditional operations。([Cloudflare Docs][7])

结果：

```text
200 → uploaded
412 → 已经被另一个 job 上传，视为成功
429 → jitter retry
```

这样不需要：

```text
HEAD → PUT
```

两个 RTT。

---

# 20. 发布矩阵不要集中到单线程 aggregator

构建仍然：

```text
                 win-x64
                 win-arm64
                 linux-x64
Build matrix ─── linux-arm64
                 osx-x64
                 osx-arm64
```

每个 runner：

```text
build
 ↓
chunk
 ↓
delta
 ↓
direct R2 CAS upload
 ↓
upload private manifest candidate
```

不要：

```text
所有 build output
      ↓
GitHub Artifact
      ↓
central job download
      ↓
再上传 R2
```

否则多一整轮大文件网络传输。

---

# 21. 但最终发布必须集中 Promotion

Matrix job 只允许写：

```text
block/*
delta/*
staging/<release-id>/*
```

不能直接改：

```text
ci-channel.json
beta-channel.json
stable-channel.json
```

最终：

```text
Promotion job
       │
       ├─ verify all 12 artifacts
       ├─ verify blockmaps
       ├─ verify signatures
       ├─ verify referenced CAS
       │
       ▼
publish releases/<tag>/
       │
       ▼
update channel pointer LAST
```

也就是：

> **数据可以高度并行发布，版本可见性必须串行提交。**

---

# 22. 发布阶段 DAG

最终 CI：

```text
                 ┌──── build win-x64 ──── chunk/delta/upload ───┐
                 │                                               │
                 ├──── build win-arm64 ─ chunk/delta/upload ────┤
                 │                                               │
commit ──────────┼──── build linux-x64 ─ chunk/delta/upload ────┤
                 │                                               │
                 ├──── build linux-arm64 ─ chunk/delta/upload ──┤
                 │                                               │
                 ├──── build osx-x64 ─── chunk/delta/upload ────┤
                 │                                               │
                 └──── build osx-arm64 ─ chunk/delta/upload ────┘
                                                                  │
                                                                  ▼
                                                             VALIDATE
                                                                  │
                                                                  ▼
                                                                SIGN
                                                                  │
                                                                  ▼
                                                               PROMOTE
                                                                  │
                                                                  ▼
                                                           channel pointer
```

---

# 23. 生成 Delta 也必须并行，但要限流

最耗 CPU 的部分可能从 gzip 转变成：

```text
VCDIFF candidate search
```

因此每个 changed target 最多测试：

```text
primary source window
+
2 alternative windows
```

即：

```text
<= 3 VCDIFF encodes
```

不要：

```text
target × 全部历史 chunks
```

否则发布耗时会爆炸。

Candidate scoring：

```text
1. same file
2. overlapping logical offset
3. old FastCDC boundary distance
4. similar size
```

前几个候选就足够。

---

# 24. Early Abort VCDIFF

非常建议加。

假设：

```text
full.gz = 200 KiB
```

采用规则：

```text
delta 最大允许 = 140 KiB
```

如果 encoder 已经产生：

```text
> 140 KiB
```

立即取消。

不用继续编码。

API 层最好支持：

```text
maxOutputBytes
CancellationToken
```

大量“不值得 delta”的块会因此提前结束。

---

# 25. Compression 和 VCDIFF 并行竞争

目标 chunk miss 后：

```text
            ┌── gzip full ─────────┐
raw target ─┤                       ├─ choose representation
            └── VCDIFF candidate ──┘
```

两者并行。

这样不用：

```text
先 gzip
等结束
再 VCDIFF
```

最终：

```text
delta < threshold → 保存 full + delta
delta bad         → 只保存 full
```

注意 full block **仍必须上传**，因为它是 fallback。

---

# 26. 不立即把 gzip 换成 zstd

v2 首发：

```text
FastCDC v2
VCDIFF
gzip
```

只改变两个核心变量。

等稳定后：

```text
v2.1
compression = zstd
```

再单独 benchmark。

否则一旦：

```text
download size
CPU
publish time
```

变化，很难知道收益来自哪一层。

---

# 27. R2 Layout

最终：

```text
block/
  00/
  01/
  ...
  ff/
      <target-sha256>

delta/
  v2/
    00/
    01/
    ...
    ff/
       <target-sha256>/
          <source-window-sha256>.vcdiff

releases/
  v1.4.5-beta/
      *.blockmap.json
      *.blockmap.json.asc

      *.blockmap.v2.json
      *.blockmap.v2.json.asc

staging/
  <release-id>/
      ...

channels/
  beta.json
  ci.json
  stable.json
```

---

# 28. v1/v2 兼容周期

发布：

```text
blockmap v1
+
blockmap v2
```

客户端：

```text
new client:
    v2
    ↓ failed/not available
    v1

old client:
    v1 only
```

至少保留若干正式 release。

v1 block：

```text
不立即 GC
```

---

# 29. GC

采用 tracing GC：

```text
所有保留 manifest
       ↓
collect referenced full blocks
       ↓
collect referenced deltas
       ↓
R2 inventory
       ↓
unreferenced set
```

然后：

```text
grace period
```

例如：

```text
7–14 days
```

后才删除。

你现在：

```text
Dead blocks = 118
Dead bytes  = 50.58 MiB
```

空间压力不大，所以 GC 可以低优先级。

---

# 30. 发布性能缓存

CI cache 保存：

```text
.pcln-update-cache/
    remote-block-index
    previous-blockmaps/
    source-window-index/
```

下一次：

```text
ETag unchanged
    ↓
直接用 cache
```

但 cache 永远只能是性能优化。

正确性依赖：

```text
conditional PUT
+
manifest validation
```

不能因为 cache 说存在就完全假设远端存在。

---

# 31. 当 R2 block 达到 100k+ 后

现在 3594 个，完整 list 很便宜。

以后不要永久：

```text
List block/*
```

改成：

```text
index/
  block/
    00.idx.zst
    01.idx.zst
    ...
    ff.idx.zst
```

256 个 SHA prefix shard。

builder 只下载本次涉及的 prefix index。

但**现在不要实现**，属于过早优化。

---

# 32. 最终性能指标

你当前 baseline：

```text
Physical blocks       3594
Physical              1219.89 MiB

v1.4.3 → v1.4.4

Full transfer         829.97 MiB
Incremental           398.37 MiB
Saved                 431.60 MiB
Byte reuse             52.00%

Average transfer       33.20 MiB/artifact
```

v2 首个验收 Gate 建议：

| 指标                       |                       目标 |
| ------------------------ | -----------------------: |
| 平均增量下载                   |    **< 20 MiB/artifact** |
| 理想目标                     | **< 10–15 MiB/artifact** |
| Byte effective reuse     |                **> 70%** |
| 理想 reuse                 |                **> 80%** |
| Missing referenced block |                    **0** |
| Target SHA mismatch      |                    **0** |
| Delta fallback 成功率       |                 **100%** |
| R2 单 block HEAD          |                    **0** |
| 重复 CAS 上传                |                     接近 0 |
| 发布流                      |               全 pipeline |
| Matrix CAS publish       |                       并行 |
| Channel promotion        |                   最后单点提交 |

---

# 实现顺序

最终就按这一条路线，不再分叉：

1. **实现 `pcln-fastcdc-v2`：128K / 512K / 1M。**
2. **blockmap format v2。**
3. **LocalBlockIndex + source-window abstraction。**
4. **接入 xdelta3 VCDIFF encoder/decoder。**
5. **实现 ±1 chunk source window，必要时扩 ±2。**
6. **最多 3 candidates，最多保存 2 deltas。**
7. **实现 70% / 16 KiB delta admission threshold。**
8. **实现 SHA-256 source/target 双验证和 full fallback。**
9. **发布器换成 S3 API。**
10. **一次 `ListObjectsV2` 建 RemoteBlockSet，取消逐块 HEAD。**
11. **`If-None-Match:*` CAS 上传。**
12. **16–48 动态并发 PUT。**
13. **FastCDC / hash / gzip / VCDIFF / upload 全部 pipeline 化。**
14. **ArrayPool/slab buffer，单次顺序扫描文件。**
15. **Matrix job 直接发布 CAS，避免 aggregator 二次传大文件。**
16. **所有 matrix 完成后单独 Validate → Sign → Promote。**
17. **v1/v2 dual publish。**
18. **CI 首先用 `1.4.3 → 1.4.4` 固定 corpus 建 benchmark gate。**
19. **跑至少若干版本后再启用 v2 GC。**
20. **最后再单独评估 gzip → zstd，不与 v2 首发混在一起。**

这就是我建议直接实施的最终架构：**FastCDC 负责稳定边界，SHA-256 CAS 负责完全复用，VCDIFF source-window 负责近似复用，S3 并发 pipeline 解决发布速度，full block 永远负责兜底。**

[1]: https://www.usenix.org/conference/atc16/technical-sessions/presentation/xia?utm_source=chatgpt.com "FastCDC: A Fast and Efficient Content-Defined Chunking Approach for Data Deduplication | USENIX"
[2]: https://github.com/jmacd/xdelta?utm_source=chatgpt.com "GitHub - jmacd/xdelta: open-source binary diff, delta/differential compression tools, VCDIFF/RFC 3284 delta compression · GitHub"
[3]: https://developers.cloudflare.com/r2/api/?utm_source=chatgpt.com "API · Cloudflare R2 docs"
[4]: https://developers.cloudflare.com/r2/get-started/s3/?utm_source=chatgpt.com "S3 · Cloudflare R2 docs"
[5]: https://developers.cloudflare.com/r2/objects/upload-objects/?utm_source=chatgpt.com "Upload objects · Cloudflare R2 docs"
[6]: https://developers.cloudflare.com/r2/platform/limits/?utm_source=chatgpt.com "Limits · Cloudflare R2 docs"
[7]: https://developers.cloudflare.com/r2/api/s3/api/ "S3 API compatibility · Cloudflare R2 docs"
