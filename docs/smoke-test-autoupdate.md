# E9 自更新烟测手册

客户端自动接收服务器上的内容/功能更新：主理人用发布脚本上传新版后，所有客户端
（主理人 + multiplayer 朋友）启动后自动拉取并暂存新版，托盘点"重启并更新"即生效。

## 1. 链路一句话

```
发布脚本(publish-release.ps1) ──SFTP──→ 云服务器 /opt/bongocat-relay/updates/
                                          ├─ manifest.json（ECDsa 签名）
                                          └─ BongoCatRemix-x.y.z-win-x64.zip
客户端启动 10s 后 ──HTTP 9438──→ GET /manifest.json → 验签+比版本 → 下载 zip
                  → SHA256 校验 → 解压暂存 update/BongoCatRemix.new.exe
                  → 托盘气泡"新版本已就绪" → 用户点"重启并更新" → 自换位重启
```

- TCP 9527：多人中继（不变）；TCP 9438：更新分发（HttpListener，与中继完全隔离）。
- 防篡改：manifest 用 ECDsa P-256 签名，公钥硬编码客户端（`UpdateSignatureVerifier`）；
  zip 强校验 SHA256；包名白名单防路径穿越；换位失败自动回滚保旧版。
- 占位：线上 manifest 当前为 version 0.0.0（客户端验签通过、比版本后判定无更新，不装半包）。

## 2. 发布端冒烟（主理人）

```powershell
# 本地自测（不发布线上，产物在 deploy\publish\update\）
powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -Version 1.1.0 -Notes "说明" -DryRun

# 真实发布（需 deploy\ssh.local.json，模板见 ssh.local.json.example）
powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -Version 1.1.0 -Notes "说明"
```

预期：构建单文件 exe（实测 ~157MB）→ zip（实测 ~63MB）→ 打印 manifest 路径 → 上传 → 完成。

验证线上（Windows 本机任意终端）：

```
curl http://your.server.ip:9438/manifest.json        # 应返回最新版本 manifest
curl -I http://your.server.ip:9438/packages/<包名>    # 应 200
curl "http://your.server.ip:9438/packages/..%2f..%2fx" # 应 400（穿越拒绝）
```

## 3. 客户端端到端（BONGOCAT_SAVE_DIR 隔离，不动真实存档）

```bash
# 1) 隔离目录 + 指向本地假服务器（或直接用线上测试）
export BONGOCAT_SAVE_DIR=D:/BongoCat/smoke-autoupdate

# 2) 启动客户端（Release 单文件 exe 或 dotnet run）
#    启动后 10 秒后台拉 manifest → 气泡"新版本已就绪，重启后生效"
#    托盘菜单出现"重启并更新"
```

检查点：

| # | 动作 | 预期 |
|---|------|------|
| 1 | 启动后等 10s | 托盘气泡：新版本已就绪；`%SAVE_DIR%/update/` 出现 `BongoCatRemix.new.exe` |
| 2 | 托盘右键 | 菜单项显示"重启并更新" |
| 3 | 点"重启并更新" | 旧进程退出 → 自动重启 → 菜单恢复"检查更新"（版本已换新）|
| 4 | 二次启动 | 无 `.old` 残留（`BongoCatRemix.exe.old` 被自动清理）|
| 5 | 手动点"检查更新"（已是最新时）| 气泡"已是最新版本" |
| 6 | 服务器关掉 9438 再点"检查更新" | 气泡更新失败警告；旧版继续正常运行 |
| 7 | 篡改 manifest 签名/包名（本地假服务器）| 客户端拒绝更新 + 日志 `[Update] 签名验证失败` |

## 4. 本地假服务器回路（可选，完全离线）

用本地 RelayServer 当更新源：`dotnet run --project src/BongoCat.RelayServer -- --port 9527 --update-port 9438 --update-dir <目录>`，
把 `manifest.json` + zip 放进 `<目录>/updates`（或 `--update-dir` 指向它），客户端设
`BONGOCAT_UPDATE_URL=http://127.0.0.1:9438` 即可全离线走通端到端。

## 5. 自动化测试覆盖

`dotnet test`（全量 403 个，原 350 + 新增 53）：

- manifest 解析/缺字段拒绝/版本边界/签名往返+篡改拒绝（App.Tests/UpdateTests、UpdateOrchestratorTests）
- 下载/SHA256/失败恢复/幂等/遗留暂存恢复（UpdateOrchestratorTests，fake HTTP + 真 ECDsa）
- 换位逻辑：正常换位/文件锁失败/.old 清理（Platform.Tests/SelfUpdateApplierTests）
- HTTP 客户端回路（HttpListener 假服务器）与 RelayServer 9438 端点回路（真 HTTP：manifest/包/穿越/405）

## 6. 服务器运维备忘

- 部署：`scripts/publish-relay.ps1` → 上传 `/opt/bongocat-relay/bongocat-relay` → `systemctl restart bongocat-relay`。
  unit 已含 `ReadWritePaths=/opt/bongocat-relay`，updates 目录在其中，无需改 unit。
- 更新包目录：`/opt/bongocat-relay/updates/`（属主 nobody:nogroup）。
- 日志：`journalctl -u bongocat-relay -f`（更新端点请求有 `[INFO] 更新端点 200 ...` 行）。
- 腾讯云安全组：TCP 9527 与 **TCP 9438** 均需放行（本次实测 9438 已可达）。
