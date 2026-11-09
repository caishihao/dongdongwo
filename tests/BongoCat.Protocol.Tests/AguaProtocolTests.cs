using BongoCat.Protocol;

using Xunit;

namespace BongoCat.Protocol.Tests;

/// <summary>
/// 阿瓜物种字节测试（GDD-09 §7）：agua=2 在 CAT_STATE / Welcome 两条链路往返保真；
/// 未知字节 Normalize 回退 cat（协议不变、payload 长度不变）。
/// </summary>
public class AguaProtocolTests
{
    [Fact]
    public void CatState_species字节agua_往返保真()
    {
        var message = new CatStateMessage(7, 0.42, 0.63, CatStyleByte.Chibi, CatSpeciesByte.Agua);
        var decoded = Assert.IsType<CatStateMessage>(Roundtrip(message));

        Assert.Equal(CatSpeciesByte.Agua, decoded.SpeciesId);
        Assert.Equal(CatStyleByte.Chibi, decoded.StyleId);
        Assert.Equal(7, decoded.MemberId);
        // GDD-09 §4：payload 长度不变（species 复用既有 1 字节尾段）
        Assert.Equal(CatStateMessage.PayloadBytes, message.MeasurePayload());
    }

    [Fact]
    public void Welcome_名册含agua_往返保真顺序不变()
    {
        var message = new WelcomeMessage(3, "X9T4CB",
        [
            new RosterEntry(1, "布丁", CatSpeciesByte.Cat),
            new RosterEntry(2, "阿瓜", CatSpeciesByte.Agua),
            new RosterEntry(3, "糯米"),
        ]);

        var welcome = Assert.IsType<WelcomeMessage>(Roundtrip(message));

        Assert.Equal(CatSpeciesByte.Cat, welcome.Members[0].Species);
        Assert.Equal(CatSpeciesByte.Agua, welcome.Members[1].Species);
        Assert.Equal(CatSpeciesByte.Cat, welcome.Members[2].Species); // 缺省 = 猫
    }

    [Fact]
    public void Normalize_未知字节回退cat_agua原样通过()
    {
        Assert.Equal(CatSpeciesByte.Agua, CatSpeciesByte.Normalize(CatSpeciesByte.Agua));
        Assert.Equal(CatSpeciesByte.Cat, CatSpeciesByte.Normalize(3));
        Assert.Equal(CatSpeciesByte.Cat, CatSpeciesByte.Normalize(200));
    }

    private static ProtocolMessage Roundtrip(ProtocolMessage message)
    {
        byte[] frame = MultiplayerProtocol.Encode(message);
        FrameParseResult result = MultiplayerProtocol.ParseFrame(frame);
        Assert.Equal(FrameParseStatus.Ok, result.Status);
        Assert.NotNull(result.Message);
        return result.Message;
    }
}
