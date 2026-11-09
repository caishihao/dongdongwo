using BongoCat.Protocol;

namespace BongoCat.Core.Net;

/// <summary>多人连接状态机（UI 据此渲染状态栏与按钮可用性）。</summary>
public enum MpConnectionState
{
    /// <summary>未启用（未点加入/已手动离开）。</summary>
    Disconnected,

    /// <summary>正在连接服务器（TCP + Hello + 等待 Welcome）。</summary>
    Connecting,

    /// <summary>已在房间（收到 Welcome，可收发同步消息）。</summary>
    Joined,

    /// <summary>断线重连退避中（指数退避后自动重试）。</summary>
    Reconnecting,

    /// <summary>加入被拒/不可恢复失败（房间码不存在/满员/服务器忙），已停止重连。</summary>
    Failed,
}

/// <summary>
/// 多人客户端接口（E8：异步 TCP + 长度前缀帧，ADR-005 决定 C 的客户端侧）。
/// 接口定义于 Core（模式同 IKeyHookService）：App 层编排只依赖此抽象，
/// TCP 实现在 Platform（MultiplayerClientService），编解码共用 BongoCat.Protocol。
/// </summary>
/// <remarks>
/// 线程纪律：Start/Stop/Send* 可在 UI 线程调用（内部加锁/Channel）；全部事件回调在
/// 后台线程触发，消费方（App 协调器）负责跳 UI 线程——Core/Platform 不依赖 WPF Dispatcher。
/// 断线语义：非主动 Stop 的断开一律指数退避重连（1s→2s→…→30s 封顶，收到 Welcome 清零）；
/// 收到 Bye 的加入拒绝（房间不存在/满员/服务器忙/字段非法）不重连，转 Failed。
/// </remarks>
public interface IMultiplayerClient : IDisposable
{
    /// <summary>当前连接状态（线程安全读）。</summary>
    MpConnectionState State { get; }

    /// <summary>服务器分配的本机成员 id（未加入房间时为 0）。</summary>
    ushort MyMemberId { get; }

    /// <summary>启动并加入房间（roomCode 空 = 创建新房间）。已运行则忽略。</summary>
    void Start(string serverAddress, string displayName, string roomCode);

    /// <summary>主动离开（断开连接，不重连）。</summary>
    void Stop();

    /// <summary>广播本机敲击（UI 线程调用；false = 当前未连接被丢弃）。</summary>
    bool SendTap(TapSide side);

    /// <summary>
    /// 广播本机猫归一化摆放位置（拖动/换风格/换物种时调用，调用方自行节流；
    /// styleByte = CatStyleByte 枚举，speciesByte = CatSpeciesByte 枚举，GDD-09 §4）。
    /// </summary>
    bool SendCatState(double normX, double normY, byte styleByte, byte speciesByte);

    /// <summary>
    /// 广播本机装扮三元组（E8 追加：加入房间成功后一次 + 换装后由调用方 500ms 防抖再发；
    /// 空串 = 该槽默认/未装备）。
    /// </summary>
    bool SendCosmetics(string hatId, string skinId, string bongosId);

    /// <summary>状态变化（含重连中）。</summary>
    event Action<MpConnectionState>? StateChanged;

    /// <summary>加入成功（Welcome）：房间码 + 全量名册（不含自己）。</summary>
    event Action<string, IReadOnlyList<RosterEntry>>? Joined;

    /// <summary>成员上线广播。</summary>
    event Action<ushort, string>? MemberJoined;

    /// <summary>成员下线广播（含被服务器踢出）。</summary>
    event Action<ushort>? MemberLeft;

    /// <summary>
    /// 远端猫摆放位置（归一化坐标 + GDD-08 style 字节 + GDD-09 species 字节；
    /// 旧客户端缺省 = CatStyleByte.Clay / CatSpeciesByte.Cat）。
    /// </summary>
    event Action<ushort, double, double, byte, byte>? RemoteCatState;

    /// <summary>远端敲击。</summary>
    event Action<ushort, TapSide, long>? RemoteTap;

    /// <summary>远端装扮三元组（E8 追加：接收方本地目录映射渲染，未知 id 回退默认）。</summary>
    event Action<ushort, string, string, string>? CosmeticSyncReceived;

    /// <summary>加入被拒（不重连，转 Failed）。</summary>
    event Action<ByeReason>? JoinRejected;

    /// <summary>连接断开（重连将自动开始；reason 供状态栏展示）。</summary>
    event Action<string>? Disconnected;
}
