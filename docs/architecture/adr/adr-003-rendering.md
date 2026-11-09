# ADR-003 猫渲染方案

状态：Proposed｜日期：2025｜决策人：程基岩

## 上下文
猫由独立部件图层组成（body / pawL / pawR / hat / bongosL / bongosR），敲击时爪部件做位移/旋转回弹动画并替换 punch 帧，另有呼吸浮动（CatBobbing）、镜像（FlipVisuals）。窗口小（~200×180 逻辑像素）、部件数 ≤ 8、动画频率低（按键触发）。需要支持装扮替换任意部件位图。

## 备选方案

### A. WPF 视觉树图层（Image × 8 + RenderTransform 动画）✅
- 每部件一个 `Image`，源为预解码冻结 `BitmapImage`（`Freeze()` 后跨线程共享、渲染线程缓存）。
- 敲击动画 = `Storyboard`（`TranslateTransform` 下压 + `RotateTransform` 小角度）+ 切换 `Image.Source` 到 punch 帧，`_hitDuration` 回弹。
- 优点：开发最快；DWM 硬件合成（ADR-001 方案 B 的前提）下 WPF 矢量/位图合成走 GPU（MILCore）；Retain 模式无需手写帧循环；装扮替换 = 换 Source，天然支持部件化。
- 缺点：元素数量大时视觉树开销高——本场景 ≤ 10 元素，远未触及；极高频重绘场景不适用（不适用≠存在此场景）。

### B. WriteableBitmap 手绘合成
- 每帧手动 blit 部件位图（Span/指针写 BGRA）。
- 优点：像素级控制、无视觉树开销。
- 缺点：所有变换/插值/透明混合自己写；每帧拷贝 CPU 开销反而高于 GPU 合成；装扮旋转/缩放插值要重新造轮子。**适用于"每帧全屏变化"的场景，本场景敲击是稀疏事件驱动，杀鸡用牛刀。**

### C. SkiaSharp / D2D 自绘
- 渲染质量与性能好，但引入原生依赖（skia natives），打包体积 +~10MB，且要自管渲染循环与窗口集成（GlacierView 之类桥）。当前无任何需求触及 WPF 上限。排除（V3 若做 GIF 生成可复用 SkiaSharp 导出，届时仅作为导出工具引入，不动主渲染）。

## 决定
**方案 A**：视觉树图层 + Storyboard 动画。结构：

```
CatRenderView (Grid, RenderTransform = 呼吸浮动)
├── Image Body      (BitmapImage, Frozen)
├── Image BongosL / BongosR
├── Image PawL / PawR   (TranslateTransform + RotateTransform, Storyboard)
└── Image Hat           (跟随 body 顶部锚点)
```

关键规则：
1. 所有部件位图在加载时 `DecodePixelWidth` 按实际显示尺寸解码 + `Freeze()`，杜绝全尺寸解码的内存浪费（一张 4K PNG 解码后 ~33MB，猫部件实际只需 ~80KB）。
2. punch 帧替换用双 `ImageSource` 字段切换引用，不重建 Image 元素。
3. 动画只动 RenderTransform，永不触发 Layout 重算（不用 Margin/Width 做动画）。
4. 镜像 = 根节点 `ScaleTransform.ScaleX = -1`。

## 后果
- 升级路径保留：若剖析（PerfView/Counters）显示合成成为瓶颈（预期不会），切换为方案 B 的 `CatRenderView.Writeable` 实现替换，`ICatAnimationController` 接口不变——接口在 module-map 已隔离。
- 动画帧素材为原创 PNG 序列（body/paw 各 2-4 帧 + punch 帧），由美术管线交付，`GameConfig` 帧时长可调。
- `Storyboard` 在窗口隐藏（猫拖到副屏/最小化）时暂停以省电：`Visibility != Visible` 时 `Pause()`。
