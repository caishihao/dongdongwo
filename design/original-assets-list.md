# 原创素材清单（Original Assets List）

> 铁律：全部素材**从零原创**，不提取、不临摹原作资源。本清单规定规格、命名与数量，供美术/音频排期。
> 命名总约定：`{类别}_{对象}_{状态/变体}.{ext}`，全小写下划线，品质后缀 `_c/_u/_r/_e/_l/_q`（unique）。

## 1. 猫本体（核心，优先级 P0）

像素风或扁平矢量（待支柱文档开放问题 1 拍板，以下按像素规格给出；矢量则 ×2 精度输出 PNG）。

| 素材 | 文件名 | 规格 | 数量 |
|---|---|---|---|
| 身体 idle（含呼吸 2 帧） | `cat_body_idle_01/02.png` | 256×256 @2x（512 实际），透明底 | 2 |
| 左爪抬起/落下 | `cat_paw_left_up.png` / `cat_paw_left_down.png` | 96×96 @2x | 2 |
| 右爪抬起/落下 | `cat_paw_right_up.png` / `cat_paw_right_down.png` | 96×96 @2x | 2 |
| 默认鼓组 | `bongos_default.png` | 192×128 @2x | 1 |
| 默认皮肤（基础猫） | `skin_base.png`（=body+paws 成套源文件） | 同上 | 1 套 |

> 层次结构：body 为底，paws/hat/bongos 独立图层合成，锚点：爪部旋转轴在腕部中心 (48,80)。

## 2. 装扮物品（V1 约 60 件）

| 类别 | 文件名模式 | 规格 | 数量 |
|---|---|---|---|
| 帽子 | `hat_{name}_{quality}.png` | 128×96 @2x，锚点头顶 (128,64) | 20（含 6 个默认色变体换色） |
| 皮肤 | `skin_{name}_{quality}.png`（成套 3 文件） | 同猫本体 | 12 套 |
| 鼓 | `bongos_{name}_{quality}.png` | 192×128 @2x | 10 |
| 表情 | `emote_{name}.png`（+喷发用 4 帧小图 `emote_{name}_f1..f4.png`） | 64×64 | 8 |
| UI 主题 | 数据驱动（见 07），仅需点缀精灵 `ui_decal_{name}.png` | 64×64 | 6 |
| 消耗品图标 | `consumable_{name}.png` | 64×64 | 4 |

品质换色规范：同一形象按品质做色相/纹样变化，用调色板文件 `palette.json` 统一管理（保证 6 品质色一致）。品质主色（原创，见 02-economy §6.1）：暖灰/苔绿/湖蓝/紫晶/琥珀金/珊瑚粉。

## 3. UI 素材

| 素材 | 文件名 | 规格 | 数量 |
|---|---|---|---|
| 宝箱（木/银/金） | `chest_wooden/silver/gold_closed.png` + `_open.png` | 128×128 | 6 |
| 金币图标 | `ui_coin.png`（+ `_flip_f1..f4` 旋转帧） | 48×48 | 5 |
| 图鉴边框/品质框 | `ui_frame_{quality}.png` | 96×96 | 6 |
| 按钮/面板九宫格 | `ui_panel.png`, `ui_btn_normal/pressed/disabled.png` | 九宫格 | 4 |
| 掉落气泡 | `ui_bubble.png` | 128×96 | 1 |
| 教程高亮/箭头 | `ui_tutorial_arrow.png` | 48×48 | 1 |
| 应用图标/托盘图标 | `app_icon.ico`, `tray_icon_{normal/move}.png` | 256/32/16 多尺寸 | 2 |

## 4. 音效（P0：MVP 只需前 4 项）

| 素材 | 文件名 | 规格 | 数量 |
|---|---|---|---|
| 鼓点·低音（左爪） | `sfx_bongo_low_01..03.wav` | WAV 44.1kHz 16bit 单声道，≤200ms | 3 变体 |
| 鼓点·高音（右爪） | `sfx_bongo_high_01..03.wav` | 同上 | 3 变体 |
| 合成出物 | `sfx_exchange_success.wav` | ≤600ms | 1 |
| 开箱 | `sfx_chest_open_{wooden/silver/gold}.wav` | ≤800ms | 3 |
| 金币入账 | `sfx_coin.wav` | ≤300ms | 1 |
| 重复折算 | `sfx_duplicate.wav` | ≤300ms | 1 |
| 领奖气泡 | `sfx_reward_pop.wav` | ≤400ms | 1 |
| 通行证升级 | `sfx_levelup.wav` | ≤800ms | 1 |
| 表情喷发 | `sfx_emote_{name}.wav` | ≤400ms | 8 |
| UI 点击/切换 | `sfx_ui_click.wav`, `sfx_ui_switch.wav` | ≤150ms | 2 |
| 鼓音色变体（随 bongos 品质，见 03 开放问题 2） | `sfx_bongo_{bongosName}_low/high_01.wav` | 同鼓点 | 按需 |

> 鼓点制作建议：真实邦戈/康加鼓采样或芯片音二选一（支柱开放问题 2）；变体间响度差 ≤ 2dB，防"抽签感"。

## 5. 字体与文本
- UI 字体：1 款开源可商用字体（如思源黑体/霞鹜文楷，待定）+ 内置回退 Microsoft YaHei。
- 全部文案原创；物品名/系列名/赛季名由文案统一编写（`items.json` 内嵌，不复制原作任何文本）。

## 6. 制作优先级与里程碑映射
| 里程碑 | 必需素材 |
|---|---|
| MVP | 猫本体全套、默认鼓、鼓点音效 6 条、托盘/应用图标 |
| V1 | 装扮 60 件、宝箱、金币/品质框、经济音效 |
| V2 | 表情、UI 主题、合成/教程 UI、其余音效 |
| V3 | 赛季物品（每赛季 ~6 件）、多人名字牌 |

## 7. 验收标准（素材通用）
- 透明底无白边；@2x 与 @1x 双导出；锚点符合本文档坐标。
- 音效首 10ms 无爆音、循环无接缝（鼓点为单发不需循环）。
- 全部文件名符合命名约定并录入 `assets-manifest.json`（文件名 → 用途 → 品质/槽位映射），供程序侧数据驱动加载。
