using System.IO;
using System.Text;
using System.Text.Json;

using BongoCat.Core.Save;
using BongoCat.Platform.Save;

using Xunit;

namespace BongoCat.Platform.Tests;

/// <summary>
/// 真 JsonSaveService 写读回环测试（E7 防回退纪律：E6 曾因 ApplyLoaded 漏拷贝丢 v6 字段，
/// 烟测才暴露——本组测试用真实服务 + 临时目录回环，守住"新版本字段必须续档"）。
/// 存档目录经 directoryOverride 注入临时路径，绝不触碰真实 %APPDATA%\BongoCatRemix。
/// </summary>
public sealed class JsonSaveServiceRoundtripTests : IDisposable
{
    private readonly string _dir;

    public JsonSaveServiceRoundtripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bongocat-save-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public void v11宠物名_写读回环_清洗后保真且checksum有效()
    {
        // E9 防回退：PetName 必须经 ApplyLoaded 拷贝；写入前清洗（控制符/超长）
        var writer = new JsonSaveService(directoryOverride: _dir);
        writer.Current.SpeciesId = "agua";
        writer.Current.PetName = "  阿瓜\u0000二世  ";
        writer.Save();

        string envelope = File.ReadAllText(Path.Combine(_dir, "save.json"));
        Assert.Contains("PetName", envelope);

        var reader = new JsonSaveService(directoryOverride: _dir);
        reader.Load();

        Assert.Equal("阿瓜二世", reader.Current.PetName);
        Assert.Equal(SaveModel.CurrentSchemaVersion, reader.Current.SchemaVersion);
    }

    [Fact]
    public void v8字段_写读回环_逐字段保真()
    {
        var writer = new JsonSaveService(directoryOverride: _dir);
        writer.Current.TutorialCompleted = true;
        writer.Current.TutorialStep = 2;
        writer.Current.UiThemeId = "ink";
        writer.Current.Coins = 555;
        writer.Current.SeasonXP = 700;
        writer.Current.EquippedHat = "hat_beret";
        writer.Current.MpServerAddress = "1.2.3.4:9437";
        writer.Current.MpDisplayName = "糯米团";
        writer.Current.MpLastRoomCode = "A7K2MQ";
        writer.Save();

        // 全新服务实例模拟重启加载
        var reader = new JsonSaveService(directoryOverride: _dir);
        reader.Load();

        Assert.True(reader.Current.TutorialCompleted);
        Assert.Equal(2, reader.Current.TutorialStep);
        Assert.Equal("ink", reader.Current.UiThemeId);
        Assert.Equal(555, reader.Current.Coins);
        Assert.Equal(700, reader.Current.SeasonXP);
        Assert.Equal("hat_beret", reader.Current.EquippedHat);
        // E8 v8：多人字段必须续档（ApplyLoaded 漏拷是 E6 教训，本回环即守门）
        Assert.Equal("1.2.3.4:9437", reader.Current.MpServerAddress);
        Assert.Equal("糯米团", reader.Current.MpDisplayName);
        Assert.Equal("A7K2MQ", reader.Current.MpLastRoomCode);
        Assert.Equal(SaveModel.CurrentSchemaVersion, reader.Current.SchemaVersion);
    }

    [Fact]
    public void v9字段_写读回环_风格选择逐字段保真()
    {
        // GDD-08 防回退纪律：v9 字段必须经 ApplyLoaded 逐字段拷贝，否则风格选择不续档
        var writer = new JsonSaveService(directoryOverride: _dir);
        writer.Current.CatStyleId = "chibi";
        writer.Current.StyleChosen = true;
        writer.Save();

        var reader = new JsonSaveService(directoryOverride: _dir);
        reader.Load();

        Assert.Equal("chibi", reader.Current.CatStyleId);
        Assert.True(reader.Current.StyleChosen);
        Assert.Equal(SaveModel.CurrentSchemaVersion, reader.Current.SchemaVersion);
    }

    [Fact]
    public void v9待选择态_写读回环_空id与未选标记保真()
    {
        var writer = new JsonSaveService(directoryOverride: _dir);
        writer.Current.CatStyleId = string.Empty;
        writer.Current.StyleChosen = false;
        writer.Save();

        var reader = new JsonSaveService(directoryOverride: _dir);
        reader.Load();

        Assert.Equal(string.Empty, reader.Current.CatStyleId);
        Assert.False(reader.Current.StyleChosen);
    }

    [Fact]
    public void v10字段_写读回环_物种选择逐字段保真()
    {
        // GDD-09 防回退纪律：v10 字段必须经 ApplyLoaded 逐字段拷贝，否则物种选择不续档
        var writer = new JsonSaveService(directoryOverride: _dir);
        writer.Current.SpeciesId = "dino";
        writer.Current.CatStyleId = "sticker";
        writer.Current.StyleChosen = true;
        writer.Save();

        var reader = new JsonSaveService(directoryOverride: _dir);
        reader.Load();

        Assert.Equal("dino", reader.Current.SpeciesId);
        Assert.Equal("sticker", reader.Current.CatStyleId);
        Assert.True(reader.Current.StyleChosen);
        Assert.Equal(SaveModel.CurrentSchemaVersion, reader.Current.SchemaVersion);
    }

    [Fact]
    public void v10存档文件_加载迁移v11_按物种补宠物名_再保存checksum有效()
    {
        var v10Model = new SaveModel
        {
            SchemaVersion = 10,
            SpeciesId = "agua",
            Coins = 99,
            CatStyleId = "chibi",
            StyleChosen = true,
        };

        WriteSaveFile(10, v10Model);

        var reader = new JsonSaveService(directoryOverride: _dir);
        reader.Load();
        Assert.Equal(SaveModel.CurrentSchemaVersion, reader.Current.SchemaVersion);
        Assert.Equal("阿瓜", reader.Current.PetName); // v11 新增字段按物种补默认名
        Assert.Equal(99, reader.Current.Coins);

        // 迁移后保存：checksum 必须按新 payload 重算，重新加载无校验错误
        reader.Save();
        var reloaded = new JsonSaveService(directoryOverride: _dir);
        reloaded.Load();
        Assert.Equal("阿瓜", reloaded.Current.PetName);
        Assert.Equal(SaveModel.CurrentSchemaVersion, reloaded.Current.SchemaVersion);
    }

    [Fact]
    public void v9存档文件_加载自动迁移v10_物种默认且风格标记清空()
    {
        // 构造 v9 载荷：已选风格（chibi）+ v9 既有状态
        var v9Model = new SaveModel
        {
            SchemaVersion = 9,
            Coins = 321,
            CatStyleId = "chibi",
            StyleChosen = true,
            TutorialCompleted = true,
            OwnedItems = new Dictionary<string, int> { ["skin_cream"] = 1 },
            EquippedSkin = "skin_cream",
        };

        WriteSaveFile(SaveModel.CurrentSchemaVersion, v9Model);

        var reader = new JsonSaveService(directoryOverride: _dir);
        reader.Load();

        Assert.Equal(SaveModel.CurrentSchemaVersion, reader.Current.SchemaVersion); // 链式迁移直达 v10
        Assert.Equal("cat", reader.Current.SpeciesId);           // v10 新增字段：默认猫
        Assert.Equal(string.Empty, reader.Current.CatStyleId);   // 刻意清空（重看升级版选择器）
        Assert.False(reader.Current.StyleChosen);                // 刻意清空（重看升级版选择器）
        Assert.Equal(321, reader.Current.Coins);                 // v9 状态保留
        Assert.True(reader.Current.TutorialCompleted);           // 教程状态不动
        Assert.Equal("skin_cream", reader.Current.EquippedSkin); // 装备不动（迁移只增不改）
    }

    [Fact]
    public void v6存档文件_加载自动迁移v8_教程主题与多人默认值生效()
    {
        // 构造 v6 载荷：UiThemeId=null（v6 存档没有该字段）+ v6 既有状态
        var v6Model = new SaveModel
        {
            SchemaVersion = 6,
            Coins = 321,
            SeasonId = "season_001",
            SeasonXP = 100,
            OwnedItems = new Dictionary<string, int> { ["skin_cream"] = 1 },
        };
        v6Model.UiThemeId = string.Empty; // 显式空 = v6 存档字段不存在
        v6Model.TutorialCompleted = false;
        v6Model.TutorialStep = 0;

        WriteSaveFile(SaveModel.CurrentSchemaVersion, v6Model);

        var reader = new JsonSaveService(directoryOverride: _dir);
        reader.Load();

        Assert.Equal(SaveModel.CurrentSchemaVersion, reader.Current.SchemaVersion); // 链式迁移直达 v8
        Assert.False(reader.Current.TutorialCompleted);          // 老玩家也会看一次教程
        Assert.Equal(0, reader.Current.TutorialStep);
        Assert.Equal("cream", reader.Current.UiThemeId);         // 主题回默认奶油
        // E8 v8 新增字段默认值
        Assert.Equal(string.Empty, reader.Current.MpServerAddress);
        Assert.Equal("猫猫", reader.Current.MpDisplayName);
        Assert.Equal(string.Empty, reader.Current.MpLastRoomCode);
        Assert.Equal(321, reader.Current.Coins);                 // v6 状态保留
        Assert.Equal("season_001", reader.Current.SeasonId);
        Assert.Equal(100, reader.Current.SeasonXP);
        Assert.Equal(1, reader.Current.OwnedItems["skin_cream"]);
    }

    [Fact]
    public void 主文件损坏_回退bak加载_数据不丢()
    {
        var writer = new JsonSaveService(directoryOverride: _dir);
        writer.Current.TutorialCompleted = true;
        writer.Current.UiThemeId = "bamboo";
        writer.Save();
        writer.Save(); // 第二次保存才轮换出 .bak（首次保存走 File.Move，无 .bak）

        File.WriteAllText(Path.Combine(_dir, "save.json"), "{ corrupt payload");

        var reader = new JsonSaveService(directoryOverride: _dir);
        reader.Load();

        Assert.True(reader.Current.TutorialCompleted);
        Assert.Equal("bamboo", reader.Current.UiThemeId); // 从 .bak 恢复（ADR-004 规则 4）
    }

    /// <summary>按 ADR-004 信封格式手写存档文件（envelope 内部类型不暴露给测试，用同构 JSON）。</summary>
    private void WriteSaveFile(int schemaVersion, SaveModel model)
    {
        string payload = JsonSerializer.Serialize(model);
        var envelope = new
        {
            SchemaVersion = schemaVersion,
            Checksum = TestCrc32.Compute(payload),
            PayloadJson = payload,
        };
        File.WriteAllText(
            Path.Combine(_dir, "save.json"),
            JsonSerializer.Serialize(envelope));
    }

    /// <summary>测试侧最小 CRC32（IEEE 802.3，与 Platform 内部实现同多项式；避免为测试暴露内部类型）。</summary>
    private static class TestCrc32
    {
        public static uint Compute(string text)
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            uint crc = 0xFFFFFFFFu;
            foreach (byte b in Encoding.UTF8.GetBytes(text))
            {
                crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }
}
