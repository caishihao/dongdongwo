# 咚咚窝 DongDongNest

> 一只小猫、小恐龙以及隐藏角色住在你的屏幕角落，安安静静地敲着邦戈鼓。
> 纯 .NET 8 / WPF 原创实现的桌面宠物游戏。

![C#](https://img.shields.io/badge/C%23-12-blue) ![.NET](https://img.shields.io/badge/.NET-8.0-purple) ![WPF](https://img.shields.io/badge/WPF-xaml-green) ![tests](https://img.shields.io/badge/tests-405%20passing-brightgreen)

## 它是什么

《咚咚窝》是一个开源的桌面宠物养成游戏：

- **两种小伙伴**：开局在猫 🐱 和恐龙 🦖 之间选择你的伙伴
- **六种艺术风格**：黏土软萌 / 贴纸 / 极简扁平 / 手绘线稿 / Q 版厚描边 / 剪纸分层——同一套渲染语言体系，参数化数据驱动
- **换装收集**：帽子、皮肤、小鼓装扮，敲鼓赚金币、开宝箱
- **好友联机**：输入好友的房间码，两只小伙伴在各自的屏幕上同步敲鼓
- **自动更新**：内置热更新，新版本发布后客户端自动同步
- **离线可玩**：不联机也自成一体的陪伴系桌宠

## 快速开始（玩家）

从 Release 页面下载最新的 `zip`，解压后双击 exe 即可——自包含发布，无需安装 .NET 运行时。

## 快速开始（开发者）

```bash
dotnet build BongoCatReplica.sln
dotnet test                        # 405 个测试
dotnet run --project src/BongoCat.UI
```

需要 .NET 8 SDK（Windows）。

## 架构一瞥

```
src/
  BongoCat.Protocol    ── 自定义二进制协议（帧头/握手/状态同步）
  BongoCat.Core        ── 领域模型（存档/装扮/风格/更新/网络抽象）
  BongoCat.Platform    ── 平台实现（JSON 存档 CRC32 校验/TCP 多人/HTTP 更新/键盘轮询）
  BongoCat.App         ── 应用服务（经济/通行证/风格渲染编排/更新编排/托盘命令）
  BongoCat.UI          ── WPF 呈现（透明分层窗口/渲染器/选择器/远端猫窗）
tests/                 ── 五个测试项目，405 用例
design/                ── 设计文档（GDD-07 教程 / GDD-08 风格包 / GDD-09 物种系统）
docs/                  ── 架构决策记录（ADR）与冒烟测试手册
```

设计上遵循三条正交轴：**风格（渲染语言）× 物种（形体）× 装扮（内容）**，
新增一种画风或一个物种都不需要动渲染器核心。

## 自托管服务器

多人联机与更新分发由一个轻量中继服务承担（`src/BongoCat.RelayServer`）：
TCP 端口承载房间与状态同步（哑中继，不解密不存储），HTTP 端口提供版本清单与更新包。
部署流程见 `deploy/DEPLOY.md`，客户端服务器地址通过多人窗口或
`BONGOCAT_UPDATE_URL` 环境变量指向你自己的服务器。

## 致谢

玩法灵感来自 [Bongo Cat](https://bongocat.com)——一只敲鼓的猫。
本项目为其原创致敬实现：全部代码、美术与设计从零编写，未使用其任何素材。
