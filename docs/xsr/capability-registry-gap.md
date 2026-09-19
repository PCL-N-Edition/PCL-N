# Machine Capability Registry 1.1 — 实现差距矩阵

对照源文档 `PCL Nexa Machine Capability Registry 1.0.md`（正文 1.1，2026-09-19）。
状态基线：`refactor/xsr` 当前实现。表格区分 Registry 契约已经落地与平台采集器仍待补齐；
不可采集的值必须保持不可用状态，不能用 `0` 或 `false` 伪装为有效观测。

图例：✅ 已接 · 🟡 部分（有骨干，缺字段/平台） · ❌ 未接

## 一、探测层（Fact / Metric）逐 namespace

| Namespace | 状态 | 已有 | 缺失（按文档小节） |
|---|---|---|---|
| platform.* (§4) | 🟡 | os/version/arch.native/arch.process | os.build、os.kernel、desktop_environment、session_type、emulation.*、windows/macos/linux 便捷布尔、compatibility.* 派生 |
| runtime.* | ✅ | framework、dynamic_code | —（文档无更多字段） |
| **jvmhost.\*** (§5) | 🟡 | §5 的 37 个 ID 已齐；IJvmHost/JvmHostService 已执行 spawn/wait/kill-tree/suspend/resume/priority/affinity；stdout/stderr ring、CPU/工作集/专用内存/线程、Windows I/O、当前会话 crash-report/hs_err 已采集 | JVM heap/native 与进程 commit 尚无可靠 collector，保持 `DependencyMissing`；cpu_sets、qos、GPU/process-tree metric 与 system correlation 仍待平台 adapter |
| **process.\*** (§6) | ❌ | 无 | 治理全集：priority/affinity/cpu_sets/qos(.performance/.efficiency)/limit.*/suspend/resume/metric.*（page_faults） |
| cpu.* (§7) | 🟡 | isa.sse2/avx2/neon、topology.logical_processors | vendor/family/model/microarchitecture/marketing_name；topology.packages/**numa_nodes**/physical_cores/smt/groups/**heterogeneous**/performance_classes/cache.l1-l3/ccd；isa 全表（avx512/avx10/aes/sha/amx/**arm.sve/sve2**）；metric.frequency/utilization/temperature/power；thermal.limit；**derived.***（hybrid/high_parallelism/numa_sensitive/performance_affinity） |
| memory.* (§8) | 🟡 | physical.usable/available、commit.total/limit/available（Win/Linux 完整，macOS=平台不支持） | physical.installed/hardware_reserved；**swap.\***（available/total/used）；**pagefile.\***（present/system_managed/current/maximum/growth_possible/safe_growth/volume/volume_free）；pressure.observable/level；numa/model(Dedicated/Unified/Hybrid)/uma；**derived.***（low_physical/commit_low/commit_near_limit/pagefile_*/system_reserve/**safe_heap_max**/unified_budget） |
| gpu.* (§9) | 🟡 | memory.dedicated.budget/current_usage/available_budget（Win DXGI） | adapters 枚举/vendor/device/architecture/type(Integrated/Discrete/External/Virtual)/primary/selected/display_attached；performance_class/low_power/high_performance/selected.performance_rank；memory.**shared**.*/recommended_working_set；api.direct3d/vulkan/metal/opengl；feature.ray_tracing/compute/mesh_shader/vrs/video_*/upscaling/**frame_generation**；metric.utilization/temperature/power/clock/local_memory/shared_memory；thermal.limit；derived.uma/hybrid/vram_pressure/shared_memory_spill/low_performance_selected/shader_pressure/resource_pressure；Linux 通道（NVML/AMD sysfs）与 macOS 通道（Metal）未接 |
| storage.* (§10) | 🟡 | device.capacity.free（实例卷） | devices 枚举/type/bus/capacity.total/rotational/removable/health；**metric.\***（sequential/random 读写、latency、iops）；io.async/mmap/direct/io_uring/directstorage；derived.fast_random_io/high_parallelism/space_low/space_critical |
| filesystem.* (§11) | 🟡 | case_sensitive/symlink/reflink(按卷判定)/path.exists/writable | hardlink/sparse_file/clone.native/copy_on_write/snapshot.native+atomic/compression/deduplication/extended_attributes/change_journal |
| display.* (§12) | 🟡 | count/internal/refresh.current/resolution | refresh.max、**dpi/scale**、hdr.supported+enabled、**vrr.supported+enabled**、color.space/depth、internal/external 枚举；derived.high_dpi/high_refresh/4k/hdr_ready/graphics_memory_factor；Linux X11(XRandR)/Wayland 通道未接 |
| power.* (§13) | 🟡 | source、battery.present/level/charging、profile.current（Win+Linux） | profile.available/performance/balanced/efficiency/hold；tdp.observable+controllable；cpu_limit/gpu_limit.controllable；macOS IOKit 通道未接 |
| thermal.* (§14) | 🟡 | cpu.temperature（Linux hwmon；Win/macOS 无免驱动通道=诚实 Unknown/未接） | pressure.observable/level(Nominal/Fair/Serious/Critical)、cpu.limit、gpu.temperature/limit、soc/storage.temperature、fan.observable/controllable/rpm；derived.cpu_high/cpu_near_limit/gpu_*/throttling/performance_degraded/pause_background/disable_prewarm |
| **formfactor.\*** (§15) | 🟡 | type/portable/battery_powered/handheld，使用电池、内建屏、触摸、键盘与手柄事实判定 | MiniPC/Workstation；derived.performance_first/battery_aware/controller_first |
| **input.\*** (§16) | 🟡 | keyboard/mouse/touch/pen/controller.available+count；Windows XInput 逐设备名称、振动能力与陀螺仪不可用事实；usage.primary/recent.keyboard/mouse/touch/controller 会话事件 | trackpad、完整 controller feature；Linux evdev force-feedback 与 macOS IOKit HID 探测 |
| **network.\*** (§17) | ❌ | 无 | interfaces/ethernet/wifi/active_interface、metered/vpn/proxy、ipv4/ipv6、internet.available、captive_portal、dns/doh、metric.latency/bandwidth/packet_loss、lan.discovery/multicast/peer_transfer |
| shell.* / security.* (§18) | ❌ | 无 | credential_vault/secure_storage、code_signature/publisher_identity、gpg.verify、hash.sha256/512、content_provenance、secret/log.redaction、vault.windows/keychain/secret_service |
| ai.* / cloud.* | ❌ | 无 | （文档仅列名，无字段定义——冻结时补） |
| java.* (§19) | 🟡 | installed、runtime.count/path/version/major | compatibility.minecraft/loader/native/hard/recommended；requirement.minimum/maximum/recommended/architecture；**derived.***（missing/non_recommended/hard_incompatible/arch_incompatible/emulated）——XSR-608 的 JavaRequirement 已有数据，缺投影 |
| **loader.\*** (§20) | 🟡 | type/version/present/complete/chain.resolved/metadata.valid；与修改页共用 receipt、版本元数据、libraries 与启动参数识别 | minecraft.compatible 在显式范围解析器接入前保持未知；requirement.minecraft_range/java_range 待接 |
| minecraft.* (§21,§24) | 🟡 | files.required/missing（客户端 jar 验证） | version/family、metadata.present+valid、main_class.valid、classpath.resolvable+entries、native.compatible、core.modified；files.integrity/corrupted/repairable/repair_status；**settings.\***（readable/render_distance/simulation_distance/mipmap/graphics_mode/fullscreen/resolution/shader/resource_packs/particles/entity_*）；settings.impact.memory/gpu/cpu+recommended |
| **mod.\*** (§22) | ❌ | 无 | count/entries/enabled/disabled；每 mod 的 file.size/data.entry_count/class.count/worldgen.entry_count/json.size/native.*；profile.content/tech/worldgen/entity/integration；runtime_profile.known+confidence；dependency.required/optional/missing、conflict.hard/soft、compatibility.loader/minecraft/java；derived 九项 |
| **resource.\*** (§23) | ❌ | 无 | effective_set/override_graph/resolution.complete；texture.*/atlas.*/model/blockstate/lang/sound.*/shader.*；derived.texture_gpu_memory/model_heap/load_peak/steady_memory |
| world.* | ❌ | 无 | （文档仅列名） |
| account.* (§29) | ❌ | 无 | available/selected、authentication.required/valid/refreshable/provider；derived.required_missing/auth_failed——AccountService 已有数据，缺 capability 投影 |
| hook.* (§30) | ❌ | 无 | prelaunch.present/required/executed/exit_code/success、postexit.present、derived.required_failed |
| machine.* (§32) | 🟡 | （Derived 层类型存在，无计算） | cpu.hybrid/high_parallelism/numa_sensitive/performance_affinity；memory.low/constrained/abundant/unified；gpu.integrated/discrete/hybrid/unified/vram_constrained/high_performance_adapter_available；storage.slow/fast/high_parallelism；display.high_dpi/high_refresh/4k/hdr；desktop/laptop/handheld/workstation/controller_first/touch_first |
| instance.* (§31) | 🟡 | path 事实在 filesystem.path.* 下 | instance.path.volume/free_space（与 storage 重复语义，需对齐归属） |

## 二、架构层缺口（比字段更根本）

| 文档小节 | 要求 | 状态 |
|---|---|---|
| §26-28 Estimator | estimate.heap/native/resource/graphics/physical/commit 全模型 + status/confidence + reason | ✅ 全 namespace、MiB 内部模型、置信度原因、版本化 profile 与 provenance 已接；未知 host 指标不参与历史校准 |
| §28 历史 | history.available/p95×5/launch_time、similarity×5、weight、calibrated.* | ✅ 有界 observation history、P95、五项相似度、权重与五项 calibrated 输出 |
| §27 estimate.status/confidence | NotStarted/Pending/Completed/Failed + Low/Medium/High + reason.unknown_mods 等 | ✅ 状态、分数、等级与五项原因均投影为 sealed capability |
| §33 Policy Engine | policy.minecraft.memory/java/cpu/gpu/priority/display/large_pages/prewarm/… + policy.nexa.* | ✅ 完整 sealed policy namespace；resolver 在 estimator 后、preflight 前运行 |
| §34-49 Preflight | 规则引擎 + aggregator（§50 管线）+ issue 模型 | ✅ §37-49 全部规则 ID、规则生产者、Issue contract、Estimated 不得 Block invariant 与显式实例 query 已接 |
| §50 Aggregator | Collect→Estimate→Resolve→Rules→Normalize→Dedupe→**因果图**→Collapse→Severity→Render Once | ✅ broker 固定 estimator→policy→preflight，随后 Normalize/Dedupe/因果折叠/Severity 并一次发布 |
| §52 Severity | Information 不参与 OverallSeverity | ✅ |
| §54-55 Remediation | remediation.* 17 个动作 + 与 Issue code 绑定 | 🟡 17 个动作精确绑定；typed handler dispatcher 强制确认和 ID 对齐，并通过 sealed XSR command 执行；内存调整、NexaCL 后台内存释放、commit 重检与实例权限重检已有 handler，其余动作等待对应安装/平台服务接入 |
| §56 Provenance | EstimateResult 携带 Value/Confidence/ModelVersion/ProfileVersion/Inputs[]/HistoricalWeight/SafetyMargin/Reasons[] | ✅ |
| §57 Profile | ResourceEstimatorProfile 可版本化参数 | ✅ profile 1.1.0 包含 baseline、coefficients、margins、quantile 与 hardware correction |
| §58 Observation | observation.launch/runtime 峰值记录（Jvm.Host 侧） | ✅ §58 全部 ID、500ms 有界采样、runtime P95、历史闭环；Heap/GPU 等未连接指标保持 `DependencyMissing` |
| §60 Namespace Registry | 冻结 allowlist | ✅ `CapabilityRegistry.Roots` 已按 §60 冻结 |
| §61 六层边界 | Fact≠Estimate≠Derived≠Policy≠Issue≠Remediation | ✅ Kind、provider ownership、projection 顺序、Issue 与 Action 路由均分层 |

## 三、建议切片顺序（依赖驱动）

1. **C1 environment 投影**（低成本高价值）：java.compatibility/derived ← XSR-608 JavaRequirement；loader.* ← 版本清单解析；account.* ← AccountService；minecraft.settings.* ← options.txt 读取。全部是已有数据的 capability 投影，无新探测。
2. **C2 derived 层**：machine.*（§32）+ memory.derived（safe_heap_max 等）+ platform.compatibility —— 纯计算，registry 的 Requirements/Derived 机制已就位（CommitAvailable 已示范）。
3. **C3 formfactor + input.usage**：桌面/掌机判定 + 最近输入模式（touch/controller preflight 的双条件前置）。
4. **C4 Estimator（已完成）**：§26-28、§56-58 的模型、历史校准与可解释 provenance。
5. **C5 Preflight（已完成）**：§34-53 的规则、聚合、因果折叠与严重度不变量。
6. **C6 Remediation（已完成）**：§54-55 的 17 个精确动作、typed handler 与 XSR command。
7. **C7 jvmhost + observation（已完成契约和可用 host 路径）**：§5/§58 完整 schema；平台尚无实现的控制和指标显式不可用，列入后续 platform adapter 工作。
8. **C8 网络/安全/资源细算**（network.*、security.*、mod.*/resource.* 扫描器）——可与 C5 并行。

> 冻结规则提醒：本文档只记录差距，不改变 §60 已冻结的 namespace 清单；新增实现必须走 provider-ownership 模式（见 machine-capability-registry.md）。
