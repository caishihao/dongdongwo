using System.Collections.ObjectModel;
using System.Windows;
using BongoCat.App;
using BongoCat.Core.Config;
using BongoCat.Core.Net;
using BongoCat.Core.Save;
using BongoCat.Protocol;

namespace BongoCat.UI;

/// <summary>
/// 多人联机窗口（E8）：服务器地址/宠物名/房间码配置 → 创建/加入；成员列表与连接状态展示。
/// E9：显示名升级为宠物名（只读回显，托盘「重命名」修改）；成员列表展示「名字 + 物种」。
/// 输入初值取存档字段（MpServerAddress/PetName/MpLastRoomCode），加入成功即回写持久化。
/// 全部事件经 Dispatcher 跳 UI 线程（Coordinator 事件在客户端后台线程触发）。
/// </summary>
public partial class MultiplayerWindow : Window
{
    private readonly MultiplayerCoordinator _mp;
    private readonly SpeciesCatalog _species;
    private readonly Action<string>? _log;
    private readonly ObservableCollection<string> _memberNames = [];

    public MultiplayerWindow(MultiplayerCoordinator mp, SpeciesCatalog species, Action<string>? log = null)
    {
        InitializeComponent();
        _mp = mp;
        _species = species;
        _log = log;

        // 存档回填（地址空 = 功能未配置，占位提示；宠物名空由当前物种默认名兜底）
        ServerBox.Text = _mp.SaveCurrent.MpServerAddress;
        ServerBox.Tag = "如 123.207.0.1:9527";
        RoomCodeBox.Text = _mp.SaveCurrent.MpLastRoomCode;
        MemberList.ItemsSource = _memberNames;
        RefreshPetName();

        _mp.StateChanged += OnMpStateChanged;
        _mp.MembersChanged += OnMpMembersChanged;
        _mp.NotificationRaised += OnMpNotification;
        _mp.JoinRejected += OnMpJoinRejected;
        OnMpStateChanged(_mp.State); // 打开窗口即同步一次当前状态（可能在窗口外已加入）

        Closed += (_, _) =>
        {
            _mp.StateChanged -= OnMpStateChanged;
            _mp.MembersChanged -= OnMpMembersChanged;
            _mp.NotificationRaised -= OnMpNotification;
            _mp.JoinRejected -= OnMpJoinRejected;
        };
    }

    /// <summary>E9：托盘改名后刷新只读回显（窗口打开期间即时生效）。</summary>
    public void RefreshPetName()
    {
        NameBox.Text = PetNameRules.Sanitize(
            _mp.SaveCurrent.PetName, _mp.SaveCurrent.DefaultPetName);
    }

    private void OnJoinClick(object sender, RoutedEventArgs e)
    {
        string server = ServerBox.Text.Trim();
        string name = PetNameRules.Sanitize(
            _mp.SaveCurrent.PetName, _mp.SaveCurrent.DefaultPetName);
        string room = RoomCodeBox.Text.Trim();

        if (server.Length == 0)
        {
            StateText.Text = "请先填写中继服务器地址（IP 或域名）";
            return;
        }

        if (room.Length > 0 && room.Length != HelloMessage.RoomCodeLength)
        {
            StateText.Text = $"房间码应为 {HelloMessage.RoomCodeLength} 位（创建房间请留空）";
            return;
        }

        StateText.Text = "连接中…";
        _mp.Join(server, name, room);
    }

    private void OnLeaveClick(object sender, RoutedEventArgs e)
    {
        _mp.Leave();
        StateText.Text = "已离开房间";
        RoomCodeText.Text = string.Empty;
    }

    private void OnMpStateChanged(MpConnectionState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMpStateChanged(state));
            return;
        }

        switch (state)
        {
            case MpConnectionState.Disconnected:
                StateText.Text = "未连接";
                RoomCodeText.Text = string.Empty;
                break;
            case MpConnectionState.Connecting:
                StateText.Text = "连接中…";
                break;
            case MpConnectionState.Reconnecting:
                StateText.Text = "连接断开，正在重连…";
                break;
            case MpConnectionState.Failed:
                StateText.Text = "加入失败（查看托盘气泡提示）";
                break;
            case MpConnectionState.Joined:
                // 房间码文本由 MembersChanged 时机（Joined 事件同步触发）填充
                StateText.Text = "已连接";
                break;
        }

        bool joined = state == MpConnectionState.Joined;
        LeaveButton.IsEnabled = joined;
        JoinButton.IsEnabled = state is MpConnectionState.Disconnected or MpConnectionState.Failed;
    }

    private void OnMpMembersChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnMpMembersChanged);
            return;
        }

        _memberNames.Clear();
        foreach ((ushort id, string name) in _mp.Members.OrderBy(kv => kv.Key))
        {
            bool self = id == _mp.MyMemberId;
            string speciesName = SpeciesNameFor(id);
            string prefix = self ? $"#{id} {name}（我）" : $"#{id} {name}";
            _memberNames.Add($"{prefix} · {speciesName}");
        }

        RoomCodeText.Text = _mp.RoomCode;
        RefreshPetName();
        _log?.Invoke($"[Mp] 成员列表刷新：{string.Join("、", _memberNames)}");
    }

    /// <summary>成员物种显示名：自己读存档，远端读最近 CAT_STATE 缓存；未知回退猫。</summary>
    private string SpeciesNameFor(ushort memberId)
    {
        byte speciesByte = memberId == _mp.MyMemberId
            ? _species.ToByte(_mp.SaveCurrent.SpeciesId)
            : _mp.TryGetRemoteSpecies(memberId) ?? CatSpeciesByte.Cat;
        return _species.TryGetByByte(CatSpeciesByte.Normalize(speciesByte), out SpeciesPack pack)
            ? pack.Name
            : PetNameRules.DefaultCatName;
    }

    private void OnMpNotification(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMpNotification(text));
            return;
        }

        StateText.Text = text;
    }

    private void OnMpJoinRejected(ByeReason reason)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMpJoinRejected(reason));
            return;
        }

        RoomCodeText.Text = string.Empty;
    }
}
