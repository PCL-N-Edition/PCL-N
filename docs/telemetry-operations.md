# PCL N 遥测后台配置手册

本文对应本体的两级数据边界：不可关闭的“基本服务数据”，以及默认关闭、可随时退出的“用户体验改进计划”。不要将基本服务数据送入 PostHog，也不要让 Feature Flag 改写同意状态、隐私规则或安全校验。

## 1. Sentry

创建两个 **.NET** Project（可在同一 Organization；若需要不同保存期限，建议拆分 Organization）：

| Project slug | GitHub Actions Secret | 接收内容 |
|---|---|---|
| `pcl-essential-diagnostics` | `SENTRY_ESSENTIAL_DSN` | 仅 `critical_failure` 最小信号；无消息、堆栈、面包屑、用户、请求、路径、日志或匿名 ID |
| `pcl-desktop` | `SENTRY_DSN` | 仅体验计划用户的清理后异常、Tracing 与 Release Health |

在 **Organization Settings → Security & Privacy** 中开启 IP 地址清洗和默认敏感字段清洗，并在两个 Project 的 Security & Privacy 中补充项目级规则。不要假设 Sentry Cloud 一定提供 Project 级保存期限：若同一 Organization 只能使用统一期限，统一设为不超过 30 天最简单；如确需让 `pcl-desktop` 保留至 90 天，请拆分 Organization 或建立可靠的定期删除流程。不要启用 Session Replay、附件、Minidump 或源码局部变量采集。

### Essential Project

建议创建一个 Dashboard：

- `Errors`：事件数，查询 `message:critical_failure`，按 `release` 显示时间序列。
- `Fatal by stage`：按 `failure_stage` 分组。
- `Fatal by platform`：按 `platform` 与 `architecture` 分组。
- `Affected releases`：按 `release`、`environment`（发布通道）分组。

告警建议：新 Issue 立即通知；同一 fingerprint 10 分钟超过 20 次时告警；单一 release 的 fatal 事件相对上一版本显著上升时通知维护者。

### Desktop Project

开启 **Releases / Release Health** 与 **Performance**。建议 Dashboard：

- Crash-free sessions，按 release 比较。
- `app.startup` 的 p50 / p95。
- `game.launch` 的 p50 / p95 与失败数。
- `download.file`、`plugin.load`、`ipc.request` 的 p95。
- Errors by release、platform、failure_stage。

初始阶段客户端对已加入计划的性能事务使用 100% 采样，便于校准链路；数据量稳定后可通过 Sentry Dynamic Sampling 降低高频成功事务，但应保留失败和新版本的代表性样本。建议告警：Crash-free sessions 低于 98%，`app.startup` p95 超过 5 秒，或新 release 的错误率高于前一版本两倍。

## 2. PostHog

创建一个独立 Project，例如 `PCL N Desktop Experience`。复制 **Project token**（不是 Personal API Key）到 GitHub Actions Secret `POSTHOG_PROJECT_TOKEN`；将实例根地址写入 `POSTHOG_HOST`，例如 `https://us.i.posthog.com`、`https://eu.i.posthog.com` 或自托管 HTTPS 地址。

客户端对每个事件设置：

- `$process_person_profile=false`：不创建 Person Profile；
- `$geoip_disable=true`：不根据 IP 生成地理属性；
- 随机匿名 `distinct_id`：退出计划时删除，重置后不关联旧记录；
- `app_version`、`release_channel`、`platform`、`architecture` 四个公共属性。

在 **Data management → Events** 为这些事件补充描述并隐藏测试环境噪声：

- `app_started`、`page_opened`；
- `game_launch_started`、`game_launch_succeeded`、`game_launch_failed`、`game_launch_cancelled`；
- `download_started`、`download_completed`、`download_failed`、`download_cancelled`；
- `update_check_completed`、`update_download_started`、`update_download_completed`、`update_download_failed`；
- `setting_feature_changed`。

### Dashboard 与漏斗

创建 Dashboard `Desktop health & adoption`：

1. `app_started` 的 Unique users，按 `app_version` 分组。
2. `page_opened` 的事件数，按 `page` 排名。
3. 游戏启动成功率：`game_launch_succeeded / game_launch_started`。
4. 下载成功率：`download_completed / download_started`，按 `category` 分组。
5. `setting_feature_changed` 按 `setting` 排名。
6. `app_started` 按 `platform`、`architecture`、`release_channel` 的分布。

创建两个 Funnel：

- `app_started → page_opened → game_launch_started → game_launch_succeeded`，窗口 1 天；
- `download_started → download_completed`，窗口 1 小时，按 `category` 分组。

### Feature Flag

Feature Flag key 使用小写点号名称，例如 `desktop.new_download_flow`。初次上线建议：内部测试 100% → beta 通道 10% → beta 50% → stable 5% → stable 100%。可按客户端传入的 `release_channel`、`platform`、`architecture` 和 `app_version` 做规则；请求失败时本体必须使用本地安全默认值。

Feature Flag 只能控制可回退的产品体验，不能开启体验计划、扩大数据范围、跳过签名/权限/认证检查或改变基本服务数据边界。

## 3. GitHub Actions Secrets

在主仓库 **Settings → Secrets and variables → Actions** 添加：

```text
SENTRY_ESSENTIAL_DSN=https://...@...ingest.sentry.io/...
SENTRY_DSN=https://...@...ingest.sentry.io/...
POSTHOG_PROJECT_TOKEN=phc_...
POSTHOG_HOST=https://us.i.posthog.com
```

可复用构建流会在 `PCL_WRITE_SECRET=1` 时写入本体。四项都只属于本体，不放入 PCL.Plugin，也不要把 Sentry Auth Token、PostHog Personal API Key 或管理密钥嵌入客户端。发布后分别触发一个受控测试异常和一组测试事件，确认两个 Sentry Project 没有串流、PostHog 没有基本服务事件，再删除测试记录。
