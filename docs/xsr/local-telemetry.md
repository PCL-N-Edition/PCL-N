# 本地构建遥测

普通 `dotnet build/run` 不会自动获得 Cloudflare API 客户端证书。CI 从 secret 恢复的
`Nexa.Desktop/Assets/api-client.pfx` 不在源码仓库中。没有证书时不会建立上传会话；
日志会明确记录“遥测未启动”，而不是把没有采集误当成服务器没有收到。

本地测试可在启动进程的环境中设置 `NEXA_API_CLIENT_CERT_PATH`，指向有私钥、未过期且
被服务端接受的 PFX。密码通过 `NEXA_API_CLIENT_CERT_PASSWORD` 传入；不要提交证书、
密码或命令历史。不要使用自签测试证书代替服务器认可的客户端身份，也不绕过 API Shield。
这两个设置已有运行时支持，不需要重新编译。CI 内嵌证书仍是没有路径覆盖时的默认来源。
Windows 在进程没有路径覆盖时也读取当前用户环境设置，避免已有终端继承旧环境。
Windows Schannel 使用 `UserKeySet` 导入，不设置 `PersistKeySet`；其他平台使用
`EphemeralKeySet`。这避免 Windows 临时私钥握手的 `SEC_E_NO_CREDENTIALS` 错误；
证书和 HTTP 连接池随会话释放。不关闭 TLS 服务端证书验证，不修改系统信任根。
相关平台限制见 [Microsoft SslStream 故障排查](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting)。

完成 OOBE、进入正常主界面后才启动遥测；`--validate-shell` 不发送遥测。
必要遥测始终启用；CI/Alpha/Beta 强制诊断，正式版诊断取决于用户设置。
启动会立即尝试上传，随后每 30 秒重试。HTTP 拒绝记录状态码，不记录响应正文、
事件内容、证书路径或密码；同一失败原因只记录一次，成功后重置。
服务器接受第一批事件后记录一次 Info 日志。未覆盖版本属性的本地构建目前为
`2.0.0.alpha.1`，管理员版本筛选需与实际构建一致。

收到“缺少证书”日志时，先解决客户端身份；收到 HTTP 状态码时再检查 API Shield、
服务端鉴权和事件校验。只有服务端接受并在管理员聚合结果中出现，才算端到端验证通过。
