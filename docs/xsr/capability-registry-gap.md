# Machine Capability Registry 1.1 — 实现差距矩阵

对照源文档 `PCL Nexa Machine Capability Registry 1.0.md`（正文 1.1，2026-09-19）。
状态基线：提交 `b401be25`，41 条能力事实在线（cpu 4 / display 4 / filesystem 5 / gpu 3 /
java 5 / memory 5 / minecraft 2 / platform 4 / power 5 / runtime 2 / storage 1 / thermal 1）。

图例：✅ 已接 · 🟡 部分（有骨干，缺字段/平台） · ❌ 未接

## 一、探测层（Fact / Metric）逐 namespace

| Namespace | 状态 | 已有 | 缺失（按文档小节） |
|---|---|---|---|
| platform.* (§4) | 🟡 | os/version/arch.native/arch.process | os.build、os.kernel、desktop_environment、session_type、emulation.*、windows/macos/linux 便捷布尔、compatibility.* 派生 |
| runtime.* | ✅ | framework、dynamic_code | —（文档无更多字段） |
| **jvmhost.\*** (§5) | ❌ | 无 | **整个 namespace**：environment(jvm_args/classpath/native_path/wrapper)、process(spawn/kill_tree/suspend/priority/affinity/qos)、io(stdout ring/encoding)、metric(cpu/memory/io/threads/gpu)、crash(exit_code/crash_report/hs_err/stderr_tail/system_correlation)。依赖 Jvm.Host 迁移（dev 侧 Jvm.NET host 未迁移） |
| **process.\*** (§6) | ❌ | 无 | 治理全集：priority/affinity/cpu_sets/qos(.performance/.efficiency)/limit.*/suspend/resume/metric.*（page_faults） |
| cpu.* (§7) | 🟡 | isa.sse2/avx2/neon、topology.logical_processors | vendor/family/model/microarchitecture/marketing_name；topology.packages/**numa_nodes**/physical_cores/smt/groups/**heterogeneous**/performance_classes/cache.l1-l3/ccd；isa 全表（avx512/avx10/aes/sha/amx/**arm.sve/sve2**）；metric.frequency/utilization/temperature/power；thermal.limit；**derived.***（hybrid/high_parallelism/numa_sensitive/performance_affinity） |
| memory.* (§8) | 🟡 | physical.usable/available、commit.total/limit/available（Win/Linux 完整，macOS=平台不支持） | physical.installed/hardware_reserved；**swap.\***（available/total/used）；**pagefile.\***（present/system_managed/current/maximum/growth_possible/safe_growth/volume/volume_free）；pressure.observable/level；numa/model(Dedicated/Unified/Hybrid)/uma；**derived.***（low_physical/commit_low/commit_near_limit/pagefile_*/system_reserve/**safe_heap_max**/unified_budget） |
| gpu.* (§9) | 🟡 | memory.dedicated.budget/current_usage/available_budget（Win DXGI） | adapters 枚举/vendor/device/architecture/type(Integrated/Discrete/External/Virtual)/primary/selected/display_attached；performance_class/low_power/high_performance/selected.performance_rank；memory.**shared**.*/recommended_working_set；api.direct3d/vulkan/metal/opengl；feature.ray_tracing/compute/mesh_shader/vrs/video_*/upscaling/**frame_generation**；metric.utilization/temperature/power/clock/local_memory/shared_memory；thermal.limit；derived.uma/hybrid/vram_pressure/shared_memory_spill/low_performance_selected/shader_pressure/resource_pressure；Linux 通道（NVML/AMD sysfs）与 macOS 通道（Metal）未接 |
| storage.* (§10) | 🟡 | device.capacity.free（实例卷） | devices 枚举/type/bus/capacity.total/rotational/removable/health；**metric.\***（sequential/random 读写、latency、iops）；io.async/mmap/direct/io_uring/directstorage；derived.fast_random_io/high_parallelism/space_low/space_critical |
| filesystem.* (§11) | 🟡 | case_sensitive/symlink/reflink(按卷判定)/path.exists/writable | hardlink/sparse_file/clone.native/copy_on_write/snapshot.native+atomic/compression/deduplication/extended_attributes/change_journal |
| display.* (§12) | 🟡 | count/internal/refresh.current/resolution | refresh.max、**dpi/scale**、hdr.supported+enabled、**vrr.supported+enabled**、color.space/depth、internal/external 枚举；derived.high_dpi/high_refresh/4k/hdr_ready/graphics_memory_factor；Linux X11(XRandR)/Wayland 通道未接 |
| power.* (§13) | 🟡 | source、battery.present/level/charging、profile.current（Win+Linux） | profile.available/performance/balanced/efficiency/hold；tdp.observable+controllable；cpu_limit/gpu_limit.controllable；macOS IOKit 通道未接 |
| thermal.* (§14) | 🟡 | cpu.temperature（Linux hwmon；Win/macOS 无免驱动通道=诚实 Unknown/未接） | pressure.observable/level(Nominal/Fair/Serious/Critical)、cpu.limit、gpu.temperature/limit、soc/storage.temperature、fan.observable/controllable/rpm；derived.cpu_high/cpu_near_limit/gpu_*/throttling/performance_degraded/pause_background/disable_prewarm |
| **formfactor.\*** (§15) | ❌ | 无 | type(Desktop/Laptop/Handheld/MiniPC/Workstation)、portable、battery_powered、handheld；derived.performance_first/battery_aware/controller_first |
| **input.\*** (§16) | ❌ | 无 | keyboard/mouse/touch/trackpad/pen/controller.available+count、gyroscope/haptics；**usage.primary/recent.\***（触屏/手柄 preflight 的前置事实）；controller feature；derived.pointer_first/touch_first/controller_first |
| **network.\*** (§17) | ❌ | 无 | interfaces/ethernet/wifi/active_interface、metered/vpn/proxy、ipv4/ipv6、internet.available、captive_portal、dns/doh、metric.latency/bandwidth/packet_loss、lan.discovery/multicast/peer_transfer |
| shell.* / security.* (§18) | ❌ | 无 | credential_vault/secure_storage、code_signature/publisher_identity、gpg.verify、hash.sha256/512、content_provenance、secret/log.redaction、vault.windows/keychain/secret_service |
| ai.* / cloud.* | ❌ | 无 | （文档仅列名，无字段定义——冻结时补） |
| java.* (§19) | 🟡 | installed、runtime.count/path/version/major | compatibility.minecraft/loader/native/hard/recommended；requirement.minimum/maximum/recommended/architecture；**derived.***（missing/non_recommended/hard_incompatible/arch_incompatible/emulated）——XSR-608 的 JavaRequirement 已有数据，缺投影 |
| **loader.\*** (§20) | ❌ | 无 | type/version/present/complete、minecraft.compatible、metadata.valid；requirement.minecraft_range/java_range；derived.missing/incompatible |
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
| §26-28 Estimator | estimate.heap/native/resource/graphics/physical/commit 全模型 + status/confidence + reason | ❌ 无任何 estimate.* 能力；Estimator 不存在 |
| §28 历史 | history.available/p95×5/launch_time、similarity×5、weight、calibrated.* | ❌ |
| §27 estimate.status/confidence | NotStarted/Pending/Completed/Failed + Low/Medium/High + reason.unknown_mods 等 | ❌（仅 preflight 不变量知道 certainty 枚举） |
| §33 Policy Engine | policy.minecraft.memory/java/cpu/gpu/priority/display/large_pages/prewarm/… + policy.nexa.* | ❌ |
| §34-49 Preflight | 规则引擎 + aggregator（§50 管线）+ issue 模型 | 🟡 Issue contract 已有 Severity/Certainty/HardConstraint/Evidence/Causes/Remediations/CanBypass/Suppressible 与 Blocked⇒Verified 不变量；缺口是规则覆盖、完整 policy 输入和更多因果边 |
| §50 Aggregator | Collect→Estimate→Resolve→Rules→Normalize→Dedupe→**因果图**→Collapse→Severity→Render Once | ❌（尤其因果图折叠未开工） |
| §52 Severity | Information 不参与 OverallSeverity | ❌ 无聚合即无该规则 |
| §54-55 Remediation | remediation.* 15 个动作 + 与 Issue code 绑定 | ❌ |
| §56 Provenance | EstimateResult 携带 Value/Confidence/ModelVersion/ProfileVersion/Inputs[]/HistoricalWeight/SafetyMargin/Reasons[] | ❌ |
| §57 Profile | ResourceEstimatorProfile 可版本化参数 | ❌ |
| §58 Observation | observation.launch/runtime 峰值记录（Jvm.Host 侧） | ❌ 依赖 jvmhost.* |
| §60 Namespace Registry | 冻结 allowlist | ✅ `CapabilityRegistry.Roots` 已按 §60 冻结 |
| §61 六层边界 | Fact≠Estimate≠Derived≠Policy≠Issue≠Remediation | 🟡 Kind 枚举齐备、broker 强制 ownership；Estimate/Policy/Issue 层无生产者 |

## 三、建议切片顺序（依赖驱动）

1. **C1 environment 投影**（低成本高价值）：java.compatibility/derived ← XSR-608 JavaRequirement；loader.* ← 版本清单解析；account.* ← AccountService；minecraft.settings.* ← options.txt 读取。全部是已有数据的 capability 投影，无新探测。
2. **C2 derived 层**：machine.*（§32）+ memory.derived（safe_heap_max 等）+ platform.compatibility —— 纯计算，registry 的 Requirements/Derived 机制已就位（CommitAvailable 已示范）。
3. **C3 formfactor + input.usage**：桌面/掌机判定 + 最近输入模式（touch/controller preflight 的双条件前置）。
4. **C4 Estimator 骨架**（§26-27）：estimate.status/confidence + heap/native 基线模型 + §56 Provenance + §57 版本化 profile——先出"低置信度"结果，preflight 由此只能 Critical 不能 Block。
5. **C5 Preflight 引擎**：issue 模型补全（Evidence/Causes/CanBypass/Suppressible）+ §50 aggregator + §51 因果图折叠 + §52 severity 规则。规则先接：JAVA_MISSING/HARD_INCOMPATIBLE（Verified 可 Block）+ MEM_HEAP_LAUNCH_LOW（Estimated 只 Critical）。
6. **C6 Remediation**（§54-55）：与 C5 的 issue code 一一绑定。
7. **C7 jvmhost.\* + observation.\***：依赖 Jvm.Host 迁移（独立大件，先行决策点）。
8. **C8 网络/安全/资源细算**（network.*、security.*、mod.*/resource.* 扫描器）——可与 C5 并行。

> 冻结规则提醒：本文档只记录差距，不改变 §60 已冻结的 namespace 清单；新增实现必须走 provider-ownership 模式（见 machine-capability-registry.md）。
