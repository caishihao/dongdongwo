using System.Windows;
using BongoCat.Core.Config;
using BongoCat.Core.Net;
using BongoCat.Platform.Window;
using BongoCat.Protocol;

namespace BongoCat.UI;

/// <summary>
/// 远程猫窗口管理（UI 层，单实例）：房间成员 ↔ RemoteCatWindow 的生命周期映射。
/// 成员上线建窗 → 远端位置/敲击/风格/装扮驱动 → 成员下线或断线渐隐关窗。
/// 必须全部在 UI 线程调用（由 App.xaml.cs 订阅 Coordinator 事件后 Dispatcher 跳线程）。
/// </summary>
public sealed class RemoteCatWindowManager
{
    private readonly ITransparentWindowService _windowService;
    private readonly StyleCatalog _styles;
    private readonly Action<string>? _log;
    private readonly Dictionary<ushort, RemoteCatWindow> _windows = new();

    public RemoteCatWindowManager(ITransparentWindowService windowService, StyleCatalog styles, Action<string>? log = null)
    {
        _windowService = windowService;
        _styles = styles;
        _log = log;
    }

    /// <summary>当前远程猫窗口数（诊断用）。</summary>
    public int WindowCount => _windows.Count;

    /// <summary>成员上线：建窗（初始位置 = 中心默认；随后收到 CatState 会归位）。</summary>
    public void OnMemberJoined(ushort memberId, string displayName)
    {
        if (_windows.ContainsKey(memberId))
        {
            return; // 重连窗口复用（服务器 id 冲突场景的防御）
        }

        var window = new RemoteCatWindow(_windowService, _styles, memberId, displayName);
        window.Show();
        // 初始位置：工作区居中偏上（等对方首个 CatState 归位；拖动过的房间秒同步）
        window.ApplyNormalizedPosition(0.5, 0.35);
        _windows[memberId] = window;
        _log?.Invoke($"[RemoteCat] 远程猫 #{memberId}「{displayName}」上桌（当前 {WindowCount} 只）");
    }

    /// <summary>远端位置更新：移动对应窗口。</summary>
    public void OnCatState(ushort memberId, double normX, double normY)
    {
        if (_windows.TryGetValue(memberId, out RemoteCatWindow? window))
        {
            window.ApplyNormalizedPosition(normX, normY);
        }
    }

    /// <summary>远端敲击：播放动画（音效由 App 接线统一播放）。</summary>
    public void OnTap(ushort memberId, TapSide side)
    {
        if (_windows.TryGetValue(memberId, out RemoteCatWindow? window))
        {
            window.PlayTap(side);
        }
    }

    /// <summary>远端风格更新（GDD-08）：byteId → 目录解析 → 窗口重载模板（保留已收到的装扮）。</summary>
    public void OnStyle(ushort memberId, byte styleByte)
    {
        if (!_windows.TryGetValue(memberId, out RemoteCatWindow? window))
        {
            return;
        }

        if (!_styles.TryGetByByte(styleByte, out StylePack style))
        {
            _log?.Invoke($"[RemoteCat] 远程猫 #{memberId} style 字节 {styleByte} 无法解析，回退 clay");
            styleByte = CatStyleByte.Clay;
            _styles.TryGetByByte(styleByte, out style);
        }

        window.ApplyStyle(style.Id);
        _log?.Invoke($"[RemoteCat] 远程猫 #{memberId} 风格 → {style.Id}");
    }

    /// <summary>远端物种更新（GDD-09 §4）：byteId → 目录解析 → 窗口重载形体模板（保留已收到的装扮与风格）。</summary>
    public void OnSpecies(ushort memberId, byte speciesByte)
    {
        if (!_windows.TryGetValue(memberId, out RemoteCatWindow? window))
        {
            return;
        }

        // 非法字节防御回退 cat（GDD-09 §4：协议层已收敛，这里双保险）
        string speciesId = speciesByte == CatSpeciesByte.Dino
            ? "dino"
            : Core.Config.SpeciesCatalog.DefaultSpeciesId;
        if (speciesByte != CatSpeciesByte.Cat && speciesId != "dino")
        {
            _log?.Invoke($"[RemoteCat] 远程猫 #{memberId} species 字节 {speciesByte} 无法解析，回退 cat");
        }

        window.ApplySpecies(speciesId);
    }

    /// <summary>远端装扮更新（E8 追加）：artKey 已由协调器清洗，窗口内复用主猫换绑逻辑。</summary>
    public void OnCosmetics(ushort memberId, string hatId, string skinId, string bongosId)
    {
        if (_windows.TryGetValue(memberId, out RemoteCatWindow? window))
        {
            window.ApplyCosmetics(hatId, skinId, bongosId);
            _log?.Invoke($"[RemoteCat] 远程猫 #{memberId} 换装：hat='{hatId}' skin='{skinId}' bongos='{bongosId}'");
        }
    }

    /// <summary>成员下线：渐隐关窗。</summary>
    public void OnMemberLeft(ushort memberId)
    {
        if (_windows.Remove(memberId, out RemoteCatWindow? window))
        {
            window.FadeOutAndClose();
            _log?.Invoke($"[RemoteCat] 远程猫 #{memberId} 渐隐离桌（剩 {WindowCount} 只）");
        }
    }

    /// <summary>断线/离开房间：全部渐隐关窗（06-multiplayer.md：断网自动清空远端猫，本地功能不受影响）。</summary>
    public void ClearAll(string reason)
    {
        foreach (RemoteCatWindow window in _windows.Values)
        {
            window.FadeOutAndClose();
        }

        int count = _windows.Count;
        _windows.Clear();
        if (count > 0)
        {
            _log?.Invoke($"[RemoteCat] 全部远程猫已渐隐（{count} 只，原因：{reason}）");
        }
    }
}
