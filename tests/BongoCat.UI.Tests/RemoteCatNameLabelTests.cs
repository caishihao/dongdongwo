using System.Windows;
using System.Windows.Controls;

using BongoCat.App.Styles;
using BongoCat.Core.Config;
using BongoCat.Platform.Window;

using Xunit;

namespace BongoCat.UI.Tests;

/// <summary>
/// E9 远端猫名字牌：清洗后的名字渲染不抛、超长省略、空名回退默认、名字牌在头顶上方。
/// WPF 视觉树在 STA 线程构建；不 Show 窗口（只验证构造与标签状态）。
/// 所有断言都在创建窗口的同一 STA 线程内完成（DependencyObject 有线程亲和性）。
/// </summary>
public class RemoteCatNameLabelTests
{
    private static readonly StyleCatalog Styles = StyleCatalogLoader.LoadFromAssembly(_ => { });

    private static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }

        return result;
    }

    [Fact]
    public void 名字牌_脏名清洗_超长省略且不抛()
    {
        var snapshot = OnSta(() =>
        {
            var window = new RemoteCatWindow(
                new FakeWindowService(), Styles, 3, "  \u0000布丁超长名字十二个字了  ");
            var text = (TextBlock)window.FindName("NameText");
            var plate = (Border)window.FindName("NamePlate");
            return (window.DisplayedPetName, text.FontSize, text.TextTrimming, plate.VerticalAlignment);
        });

        Assert.Equal("布丁超长名字十二", snapshot.DisplayedPetName);
        Assert.Equal(11, snapshot.FontSize);
        Assert.Equal(TextTrimming.CharacterEllipsis, snapshot.TextTrimming);
        Assert.Equal(VerticalAlignment.Top, snapshot.VerticalAlignment); // E9：头顶上方
    }

    [Fact]
    public void 名字牌_空名回退猫猫()
    {
        string name = OnSta(() => new RemoteCatWindow(
            new FakeWindowService(), Styles, 4, "   ").DisplayedPetName);

        Assert.Equal("猫猫", name);
    }

    [Fact]
    public void 名字牌_运行期更新_重新清洗()
    {
        string name = OnSta(() =>
        {
            var window = new RemoteCatWindow(new FakeWindowService(), Styles, 5, "布丁");
            window.UpdatePetName("🐱🐱🐱🐱🐱🐱🐱");
            return window.DisplayedPetName;
        });

        Assert.Equal("🐱🐱🐱🐱🐱🐱", name);
    }

    /// <summary>透明窗口服务假实现：测试不建 HWND，全部方法返回安全默认值。</summary>
    private sealed class FakeWindowService : ITransparentWindowService
    {
        public TransparencyMode ActiveMode => TransparencyMode.Dwm;

        public bool TryEnableDwmTransparency(nint hwnd) => true;

        public void ApplyPetWindowStyles(nint hwnd)
        {
        }

        public void EnableClickThrough(nint hwnd)
        {
        }

        public void SetWindowScreenPos(nint hwnd, int x, int y)
        {
        }

        public bool TryGetCursorScreenPos(out int x, out int y)
        {
            x = 0;
            y = 0;
            return false;
        }

        public bool TryRegisterQuitHotkey(nint hwnd, int hotkeyId) => false;

        public bool TryGetWorkAreaPixels(nint hwnd, out int x, out int y, out int width, out int height)
        {
            x = 0;
            y = 0;
            width = 1920;
            height = 1080;
            return true;
        }

        public bool TryGetWindowRectPixels(nint hwnd, out int x, out int y, out int width, out int height)
        {
            x = 0;
            y = 0;
            width = 110;
            height = 100;
            return true;
        }
    }
}
