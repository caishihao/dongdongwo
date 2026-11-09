using System.Threading.Channels;

namespace BongoCat.Core.Input;

/// <summary>敲击用哪只爪（0=左，1=右，与 module-map 接口草案对齐）。</summary>
public enum PawSide
{
    /// <summary>左爪（低音鼓）。</summary>
    Left = 0,

    /// <summary>右爪（高音鼓）。</summary>
    Right = 1,
}

/// <summary>
/// 按键按下事件（上升沿）。struct 传递：热路径零分配（Platform/ 热路径规则，ADR-002）。
/// 玩法只依赖"有按键"，不依赖具体键值（ADR-002 后果节）。
/// </summary>
/// <param name="VirtualKey">Win32 虚拟键码（0x08~0xFE）。</param>
/// <param name="TimestampTicks">Stopwatch.GetTimestamp() 时间戳。</param>
public readonly record struct KeyPressEvent(int VirtualKey, long TimestampTicks);

/// <summary>
/// 全局键盘捕获服务（ADR-002：GetAsyncKeyState 轮询实现，Platform 层提供）。
/// 事件经 Channel 投递到 UI 线程消费；接口定义在 Core，便于 App 层编排与测试伪造。
/// </summary>
/// <remarks>
/// 与 module-map 草案的偏差说明：事件从 <c>event Action&lt;KeyPressEvent&gt;</c>
/// 改为 <see cref="Reader"/>（ChannelReader）。原因：轮询线程直接发 .NET 事件会把
/// 消费者绑死在轮询线程上，UI 动画必须再手动跳线程；Channel 让"投递"与"消费线程"
/// 解耦，消费者（UI）用 await foreach 拉取即可，天然满足 ADR-002 的零分配投递。
/// </remarks>
public interface IKeyHookService : IDisposable
{
    /// <summary>轮询是否运行中。</summary>
    bool IsRunning { get; }

    /// <summary>按键事件只读通道（单写单读）。</summary>
    ChannelReader<KeyPressEvent> Reader { get; }

    /// <summary>启动轮询线程（幂等：已运行则忽略）。</summary>
    void Start();

    /// <summary>停止轮询线程（设置中关闭监听用）。</summary>
    void Stop();
}
