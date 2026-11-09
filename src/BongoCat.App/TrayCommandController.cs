using BongoCat.Core.Audio;
using BongoCat.Core.Save;

namespace BongoCat.App;

/// <summary>
/// 托盘菜单命令编排（epics E2-S2 设置项持久化的用例层）。
/// 纯逻辑、零 UI/Win32 依赖：所有菜单命令在 <see cref="ISaveService"/> 内存模型与
/// <see cref="IAudioService"/> 上生效，窗口动作（显示/隐藏/缩放/退出）以事件广播，
/// 由 UI 层订阅执行——保持 App 层不引用视图类型（architecture.md 分层规则）。
/// </summary>
/// <remarks>
/// 落盘策略：
/// - 开关类命令（静音/镜像/尺寸/退出）一次性动作 → 命令内立即 Save；
/// - 音量滑条是连续值 → <see cref="SetVolume"/> 只改内存模型 + 即时生效，
///   由 UI 侧防抖后调 <see cref="PersistSettings"/> 落盘，避免拖动滑条期间高频磁盘写入。
/// </remarks>
public sealed class TrayCommandController
{
    private readonly ISaveService _save;
    private readonly IAudioService _audio;

    /// <summary>请求显示/隐藏猫窗口（参数 = 显示与否；UI 订阅执行窗口 Hide/Show + 位置重算）。</summary>
    public event Action<bool>? VisibilityRequested;

    /// <summary>水平镜像状态变化（参数 = 新状态；UI 订阅驱动 RenderTransform）。</summary>
    public event Action<bool>? MirroredChanged;

    /// <summary>窗口尺寸档位变化（参数 = 新缩放；UI 订阅改窗口物理尺寸并重算位置比例）。</summary>
    public event Action<double>? ScaleChanged;

    /// <summary>音量变化（参数 = 新音量 0~1；UI 订阅同步滑条显示）。</summary>
    public event Action<float>? VolumeChanged;

    /// <summary>请求退出应用（UI 订阅：保存窗口位置 → Save → Shutdown）。</summary>
    public event Action? ExitRequested;

    /// <summary>请求打开背包/图鉴窗口（E3；UI 订阅创建/激活 InventoryWindow）。</summary>
    public event Action? InventoryRequested;

    /// <summary>请求打开通行证窗口（E6；UI 订阅创建/激活 PassWindow）。</summary>
    public event Action? PassRequested;

    /// <summary>请求重播新手教程（E7；完成态也能重看，UI 订阅驱动教程气泡）。</summary>
    public event Action? TutorialReplayRequested;

    /// <summary>请求打开多人联机窗口（E8；UI 订阅创建/激活 MultiplayerWindow）。</summary>
    public event Action? MultiplayerRequested;

    /// <summary>请求检查更新（托盘"检查更新"；App 订阅驱动 UpdateOrchestrator，异步）。</summary>
    public event Action? CheckUpdateRequested;

    /// <summary>请求"重启并更新"（暂存就绪时托盘菜单触发；UI 订阅执行换位重启流程）。</summary>
    public event Action? RestartUpdateRequested;

    /// <summary>是否有已暂存待生效的更新（App 依据 UpdateOrchestrator 状态设置；菜单文案切换用）。</summary>
    public bool HasPendingUpdate { get; set; }

    /// <summary>猫风格变更（GDD-08：托盘"形象"/首启选择器确认；参数 = 新风格 id；UI 订阅重载渲染模板）。</summary>
    public event Action<string>? StyleChanged;

    /// <summary>
    /// 猫物种变更（GDD-09：托盘"形象▸物种"/首启选择器页签；参数 = 新物种 id；
    /// UI 订阅重载渲染模板，App 层同时接物种限定装备校验与多人 CatState 重发）。
    /// </summary>
    public event Action<string>? SpeciesChanged;

    /// <summary>宠物名变更（E9；参数 = 清洗后的名字；UI/联机层订阅刷新本地展示）。</summary>
    public event Action<string>? PetNameChanged;

    /// <summary>请求重命名宠物（托盘"重命名"；UI 层弹小输入框后回调 <see cref="SetPetName"/>）。</summary>
    public event Action? RenameRequested;

    /// <summary>猫窗口当前是否可见（菜单文案切换用）。</summary>
    public bool IsCatVisible { get; private set; } = true;

    public TrayCommandController(ISaveService save, IAudioService audio)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
    }

    /// <summary>存档模型（UI 读取做菜单勾选态/初始值）。</summary>
    public SaveModel Save => _save.Current;

    /// <summary>
    /// 启动时把已加载的存档设置同步进音频服务（E2 之前音量/静音只进存档不生效）。
    /// 必须在 <see cref="ISaveService.Load"/> 之后调用一次。
    /// </summary>
    public void ApplyStartupSettings()
    {
        _audio.MasterVolume = _save.Current.MasterVolume;
        _audio.Muted = _save.Current.Muted;
    }

    /// <summary>显示/隐藏猫。隐藏不停止轮询与存档（再显示按存档比例重算位置还原）。</summary>
    public void ToggleCatVisible()
    {
        IsCatVisible = !IsCatVisible;
        VisibilityRequested?.Invoke(IsCatVisible);
    }

    /// <summary>切换静音并立即落盘。解除静音时恢复存档音量。</summary>
    public void ToggleMute()
    {
        _save.Current.Muted = !_save.Current.Muted;
        _audio.Muted = _save.Current.Muted;
        if (!_save.Current.Muted)
        {
            _audio.MasterVolume = _save.Current.MasterVolume;
        }

        _save.Save();
    }

    /// <summary>
    /// 调整主音量（0~1 自动钳制）：即时生效到音频服务 + 写内存模型，但不落盘——
    /// 滑条连续拖动由 UI 防抖后调用 <see cref="PersistSettings"/>。
    /// </summary>
    public void SetVolume(float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        _save.Current.MasterVolume = volume;
        _audio.MasterVolume = volume;
        VolumeChanged?.Invoke(volume);
    }

    /// <summary>把内存中的设置（音量防抖等）统一落盘。</summary>
    public void PersistSettings() => _save.Save();

    /// <summary>设置水平镜像并立即落盘。</summary>
    public void SetMirrored(bool mirrored)
    {
        _save.Current.CatMirrored = mirrored;
        MirroredChanged?.Invoke(mirrored);
        _save.Save();
    }

    /// <summary>
    /// 设置窗口尺寸档位并立即落盘。非法值收敛到 100%（防御手改存档/传参）。
    /// UI 订阅 <see cref="ScaleChanged"/> 后：改窗口物理尺寸 → 用物理像素重算位置 clamp → 保存位置比例。
    /// </summary>
    public void SetWindowScale(double scale)
    {
        if (!SaveModel.IsValidWindowScale(scale))
        {
            scale = 1.0;
        }

        _save.Current.WindowScale = scale;
        ScaleChanged?.Invoke(scale);
        _save.Save();
    }

    /// <summary>请求退出（E2 起：退出唯一入口走托盘菜单，根治 WM_CLOSE 不退的边界）。</summary>
    public void Exit()
    {
        ExitRequested?.Invoke();
        _save.Save(); // 兜底：即便 UI 层漏保存设置，此处也有最后一次落盘
    }

    /// <summary>打开背包/图鉴窗口（E3：窗口动作广播事件，UI 层订阅执行——本控制器不引用视图）。</summary>
    public void OpenInventory() => InventoryRequested?.Invoke();

    /// <summary>打开通行证窗口（E6：与背包同模式广播事件）。</summary>
    public void OpenPass() => PassRequested?.Invoke();

    /// <summary>重播新手教程（E7：与开窗同模式广播事件，App 订阅驱动教程气泡重看）。</summary>
    public void ReplayTutorial() => TutorialReplayRequested?.Invoke();

    /// <summary>打开多人联机窗口（E8：与背包同模式广播事件，UI 层订阅执行）。</summary>
    public void OpenMultiplayer() => MultiplayerRequested?.Invoke();

    /// <summary>托盘"检查更新"（无暂存时）→ 广播事件，App 层异步执行检查并回弹气泡。</summary>
    public void CheckForUpdate() => CheckUpdateRequested?.Invoke();

    /// <summary>托盘"重启并更新"（暂存就绪时）→ 广播事件，UI 层执行换位重启流程。</summary>
    public void RestartAndUpdate() => RestartUpdateRequested?.Invoke();

    /// <summary>
    /// 设置猫风格并立即落盘（GDD-08 §3.3 托盘"形象"子菜单 + 首启选择器确认共用）。
    /// 同时置 StyleChosen=true（防重复弹选择器的独立标记，GDD-08 §3.1）；
    /// 广播 StyleChanged → UI 重载模板；多人上行由 App 层接线 NotifyStyleChanged。
    /// </summary>
    public void SetCatStyle(string styleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);

        _save.Current.CatStyleId = styleId;
        _save.Current.StyleChosen = true;
        StyleChanged?.Invoke(styleId);
        _save.Save();
    }

    /// <summary>
    /// 设置猫物种并立即落盘（GDD-09 §3.3 托盘"形象▸物种"子菜单 + 首启选择器页签共用）。
    /// 同时置 StyleChosen=true（物种与风格在选择器中一次同时确定，GDD-09 §3.2）；
    /// 广播 SpeciesChanged → UI 重载模板 + 物种限定装备校验；多人上行由 App 层接线重发 CAT_STATE。
    /// 非空防御与 SetCatStyle 同纪律：id 合法性由调用方经 SpeciesCatalog 校验。
    /// </summary>
    public void SetCatSpecies(string speciesId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speciesId);

        _save.Current.SpeciesId = speciesId;
        _save.Current.StyleChosen = true;
        SpeciesChanged?.Invoke(speciesId);
        _save.Save();
    }

    /// <summary>请求打开重命名输入框（托盘菜单；UI 订阅弹窗）。</summary>
    public void RequestRename() => RenameRequested?.Invoke();

    /// <summary>
    /// 设置宠物名字并立即落盘（首登选择器 / 托盘重命名共用；E9）。
    /// 统一经 <see cref="PetNameRules.Sanitize"/>：trim、去控制符、12 字素、UTF-8 ≤24 字节，
    /// 空/全脏 → 当前物种默认名。改名只影响之后的名册快照（联机中下次进入房间生效，
    /// 协议无改名消息——保持旧端兼容，见 06-multiplayer.md §6.3）。
    /// </summary>
    public void SetPetName(string rawName)
    {
        string name = PetNameRules.Sanitize(rawName, PetNameRules.DefaultNameForSpecies(_save.Current.SpeciesId));
        _save.Current.PetName = name;
        // 兼容 v8 遗留显示名字段：两者语义已合并为宠物名，旧读取路径不会拿到过期值
        _save.Current.MpDisplayName = name;
        PetNameChanged?.Invoke(name);
        _save.Save();
    }
}
