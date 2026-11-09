using System.Collections.ObjectModel;
using System.Windows;
using BongoCat.App;
using BongoCat.Core.Net;
using BongoCat.Protocol;

namespace BongoCat.UI;

/// <summary>
/// 多人联机窗口（E8）：服务器地址/显示名/房间码配置 → 创建/加入；成员列表与连接状态展示。
/// 输入初值取存档 v8 字段（MpServerAddress/MpDisplayName/MpLastRoomCode），加入成功即回写持久化。
/// 全部事件经 Dispatcher 跳 UI 线程（Coordinator 事件在客户端后台线程触发）。
/// </summary>
public partial class MultiplayerWindow : Window
{
    private readonly MultiplayerCoordinator _mp;
    private readonly Action<string>? _log;
    private readonly ObservableCollection<string> _memberNames = [];

    public MultiplayerWindow(MultiplayerCoordinator mp, Action<string>? log = null)
    {
        InitializeComponent();
        _mp = mp;
        _log = log;

        // 存档回填（v8 字段：地址空 = 功能未配置，占位提示；显示名空回退"猫猫"）
        ServerBox.Text = _mp.SaveCurrent.MpServerAddress;
        ServerBox.Tag = "如 123.207.0.1:9527";
        NameBox.Text = string.IsNullOrEmpty(_mp.SaveCurrent.MpDisplayName) ? "猫猫" : _mp.SaveCurrent.MpDisplayName;
        RoomCodeBox.Text = _mp.SaveCurrent.MpLastRoomCode;
        MemberList.ItemsSource = _memberNames;

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

    private void OnJoinClick(object sender, RoutedEventArgs e)
    {
        string server = ServerBox.Text.Trim();
        string name = NameBox.Text.Trim();
        string room = RoomCodeBox.Text.Trim();

        if (server.Length == 0)
        {
            StateText.Text = "请先填写中继服务器地址（IP 或域名）";
            return;
        }

        if (name.Length == 0)
        {
            StateText.Text = "请先填写显示名";
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
            _memberNames.Add(self ? $"#{id} {name}（我）" : $"#{id} {name}");
        }

        RoomCodeText.Text = _mp.RoomCode;
        _log?.Invoke($"[Mp] 成员列表刷新：{string.Join("、", _memberNames)}");
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
