# E8 部署指南：腾讯云中继服务器

把 BongoCat 云中继（`BongoCat.RelayServer`）部署到你的腾讯云服务器，朋友凭 6 位房间码跨互联网把各自的猫摆到同一张桌面。

## 1. 架构一句话

```
朋友A客户端 ─┐
朋友B客户端 ─┼─ TCP 9527 ─→ 腾讯云 bongocat-relay（哑中继 + 房间登记）─→ 各自桌面上的远程猫
你本机 ─────┘
```

服务器只做**消息路由**（不理解猫的语义）+ 房间登记（房间码/满员/心跳踢出/空闲回收），不存任何持久数据。

## 2. 产物与上传

Windows 本机（项目根目录）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish-relay.ps1
```

产出 `deploy\publish\bongocat-relay`（linux-x64 自包含单文件，目标机**无需安装 .NET**）。

上传（替换 `<server>` 为服务器公网 IP）：

```powershell
scp deploy\publish\bongocat-relay root@<server>:/opt/bongocat-relay/bongocat-relay
scp deploy\bongocat-relay.service root@<server>:/etc/systemd/system/
```

服务器上赋可执行权限：

```bash
ssh root@<server> "chmod +x /opt/bongocat-relay/bongocat-relay"
```

## 3. systemd 启用

前置（必须，否则服务会因写 relay.log 无权限而崩溃循环）：

```bash
chown nobody:nogroup /opt/bongocat-relay
```

> 原因：服务以 `nobody:nogroup` 运行，unit 里的 `ReadWritePaths` 只解除沙箱对目录的写限制、**不解文件属主权限**——目录若由 root 创建（root 上传二进制时默认属主 root），nobody 进程无法在目录内创建/写 `relay.log`，`File.OpenHandle` 抛 UnauthorizedAccess 导致 status=6/ABRT 反复重启。

```bash
systemctl daemon-reload
systemctl enable --now bongocat-relay
systemctl status bongocat-relay          # 确认 active (running)
journalctl -u bongocat-relay -f          # 看实时日志（连接/加入/踢出）
```

说明：

- unit 内 `Restart=always`：进程崩溃/被杀 3 秒后自动拉起。
- 停止（`systemctl stop`）走 SIGTERM：中继先给所有在线会话广播 `Bye(ServerShutdown)` 再退出（优雅关闭，客户端收到后会自动重连）。
- 想改端口/限额：编辑 unit 的 `ExecStart` 行追加参数（如 `--port 9527 --max-rooms 32`），全部参数见 `bongocat-relay --help`。

## 4. 腾讯云安全组放行（关键步骤，漏了连不上）

控制台 → 云服务器 → 安全组 → 入站规则 → 添加：

| 项 | 值 |
|---|---|
| 协议端口 | TCP 9527 |
| 协议端口 | TCP 9438（E9 更新分发 HTTP 端点，客户端自动更新走这里） |
| 来源 | 0.0.0.0/0（见下方安全说明，可收紧为朋友们的出口 IP） |
| 策略 | 允许 |

> 更新分发：把更新包和 manifest 上传到 `/opt/bongocat-relay/updates/`（用
> `scripts\publish-release.ps1` 一条命令完成），客户端启动 10s 后自动拉取。
> 详细冒烟流程见 `docs/smoke-test-autoupdate.md`。

## 5. 客户端验证

1. 启动 BongoCat 桌宠 → 托盘右键 → **多人**；
2. 中继服务器地址填 `服务器公网IP`（端口默认 9527 可省略，带端口写 `IP:9527`）；
3. 显示名随意 → **创建房间** → 窗口显示 6 位房间码；
4. 把房间码告诉朋友：对方同样打开"多人"，填同一服务器地址 + 房间码 → **加入房间**；
5. 双方桌面应出现对方的远程猫（简笔猫头 + 名字牌）；敲键盘对方猫会敲、拖猫双方位置同步。

## 6. 安全说明（务必读）

- **无认证体系：6 位房间码即临时凭证**。知道房间码且能连到服务器的人都能加入，房间码别发公开群。
- **明文 TCP**：消息只含显示名/坐标/敲击侧别，**不含任何敏感数据**（无密码、无文件、无存档内容），明文可接受；但请不要在联机网络里输入隐私信息当显示名。
- **建议安全组限源 IP**：朋友数量少时，把入站规则"来源"从 0.0.0.0/0 收紧为朋友们的出口 IP 列表，等于免费的访问控制。
- **服务器面**：中继有帧率限制（默认 50 帧/s 超限断开）、帧大小上限 256KB、并发房间上限 64、单房 8 人、心跳 60s 踢出、空房 10 分钟回收——防刷防耗尽的最小权威集。
- **后续可加 TLS**：协议帧格式与传输无关（ADR-005），未来需要加密时可用 SslStream 包一层或套 Nginx stunnel，不改消息集。
- 防火墙（腾讯云若有系统级 firewalld）同样需要放行：`firewall-cmd --add-port=9527/tcp --permanent && firewall-cmd --reload`。

## 7. 常见问题

| 现象 | 排查 |
|---|---|
| 客户端一直"连接中…" | 安全组/防火墙没放行 9527；`systemctl status bongocat-relay` 确认进程活着 |
| 加入提示"房间不存在" | 房间码敲错（0/O、1/I/L 已从字符集剔除仍建议复制粘贴）；或房间空置超 10 分钟被回收，让房主重新创建 |
| 提示"房间已满" | 单房上限 8 人，已达上限（unit 里 `--max-members` 可调） |
| 远程猫不动了 | 双方 60s 无流量会被踢；客户端 20s 心跳正常时不会发生——检查网络波动，客户端会自动重连 |
| 服务器重启后房间码变了 | 房间是内存态（会话纯临时，不持久化），重启即清——设计如此 |
