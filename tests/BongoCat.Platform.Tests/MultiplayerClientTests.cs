using BongoCat.Core.Net;
using BongoCat.Platform.Net;

using Xunit;

namespace BongoCat.Platform.Tests;

/// <summary>
/// E8 多人客户端纯逻辑测试：断线重连指数退避（public static，无需真实连接）。
/// 完整连接生命周期由 docs/smoke-test-e8.md 单机双开烟测覆盖（真实 TCP 集成场景）。
/// </summary>
public class MultiplayerClientTests
{
    [Theory]
    [InlineData(1, 1000)]   // 第 1 次重连等 1s
    [InlineData(2, 2000)]   // 指数翻倍
    [InlineData(3, 4000)]
    [InlineData(4, 8000)]
    [InlineData(5, 16000)]
    [InlineData(6, 30000)]  // 封顶
    [InlineData(7, 30000)]
    [InlineData(50, 30000)]
    public void 断线重连退避_指数翻倍_封顶30秒(int attempt, int expectedMs)
    {
        Assert.Equal(expectedMs, MultiplayerClientService.ComputeBackoffDelayMs(attempt));
    }

    [Fact]
    public void 心跳间隔常量_20秒_对服务器60s超时有3次容错余量()
    {
        // 心跳 20s，服务器默认 60s 无帧踢出：丢两次心跳仍存活
        Assert.Equal(TimeSpan.FromSeconds(20), MultiplayerClientService.HeartbeatInterval);
        Assert.Equal(20, MultiplayerClientService.HeartbeatInterval.TotalSeconds);
        Assert.True(3 * MultiplayerClientService.HeartbeatInterval.TotalSeconds <= 60);
    }
}
