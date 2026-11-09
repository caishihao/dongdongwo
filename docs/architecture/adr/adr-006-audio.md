# ADR-006 音频方案

状态：Proposed｜日期：2025｜决策人：程基岩

## 上下文
核心循环每次按键播放一个鼓点音效（原作 `RandomSound`，池：beat/minus/tap 类变体）。特征：短样本（< 1s）、WAV、触发频率 = 玩家打字频率（峰值 ~10-15 次/秒）、要求极低延迟与零爆音、支持音量与静音设置。未来可能加背景音乐与表情语音（多路混音）。

## 备选方案

### A. WPF MediaPlayer / SoundPlayer
- `SoundPlayer`：仅 WAV，但每次 Play 走完整文件加载，快速连击下卡顿、无法混音（同音效连击会截断）。排除。
- `MediaPlayer`：基于 MediaElement 管线，延迟高（数十至数百 ms）、实例重、无法低延迟精确触发。排除。

### B. NAudio（WaveOutEvent / WasapiOut + 缓冲池）
- 优点：纯托管封装、文档多、混音（MixingSampleProvider）现成。
- 缺点：第三方依赖；在 WASAPI 共享模式下仍是不错的封装层。

### C. WASAPI 直接 P/Invoke（共享模式 + IAudioClient）✅
- 预解码全部音效到内存 PCM；共享模式低延迟渲染；混音 = 手动叠加 PCM 或多个 SourceVoice 语义。
- 缺点：COM 互操作工程量高于 NAudio。

### 权衡与决定
**决定：NAudio（方案 B）作为 V1 实现，接口按 C 的能力面设计**。理由：NAudio 本身就是 WASAPI 封装（WasapiOut），延迟对"敲鼓跟手"完全够用（共享模式 < 30ms，人耳感知阈值之上）；自写 WASAPI COM 层省下的依赖不值得多花一周。若未来需要 < 10ms 竞技级延迟（不存在此需求）再换 C，`IAudioService` 接口不变。

### 实现规则（逐条可执行）
1. 音效文件：原创 WAV 22050Hz/16bit/mono，启动时全量预解码进 `Dictionary<SoundId, float[]>`，运行时零文件 IO。
2. 播放 = 从池中取空闲 `CachedSound` 实例投入 `MixingSampleProvider`，**不创建新对象**（热路径零分配规则同样适用）。
3. 随机池：`SoundPool` 按 soundGroup 从池中不重复随机（洗牌袋算法），连击不"复读机"（对应原作随机池机制，原创实现）。
4. 同帧去抖：8ms 内同组重复触发合并（轮询 8ms 天然去重，双保险）。
5. 主音量经 `VolumeSampleProvider`，设置持久化走 settings.json（ADR-004）。
6. 音频设备变更（拔耳机）：监听 `MMDeviceEnumerator.DefaultDeviceChanged` 重建设备输出，期间事件排队不丢。

## 后果
- 新增 NuGet 依赖 NAudio（MIT，纯托管，无原生负担，杀软友好）。
- 音频线程与 UI 线程隔离：`IAudioService.Play()` 线程安全、非阻塞。
- 测试策略：`IAudioService` 的假实现计数触发次数，供 CatDirector 编排测试验证"每次按键恰好一个音效"。
