using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BongoCat.App.Styles;
using BongoCat.Core.Config;
using BongoCat.Core.Input;
using BongoCat.Core.Save;
using BongoCat.UI.Cosmetics;

namespace BongoCat.UI;

/// <summary>
/// 首启物种×风格选择器（GDD-09 §3）：顶部物种页签（猫/恐龙）+ 6 风格卡片，单选 → 确认 / 跳过。
/// 选择状态 = (species, style) 组合：切页签不清空风格选择（GDD-09 §3.1）。
/// <see cref="SelectedSpeciesId"/> / <see cref="SelectedStyleId"/> 为结果（ShowDialog 返回 true =
/// 已确认/跳过）；存档写入由调用方（App.OnStartup）经 TrayCommandController 完成（单一写入路径）。
/// 微动画：只动画选中卡（低配降级——首启 12 个预览的性能峰值直接规避，GDD-09 §8 边界 6）。
/// </summary>
public partial class StylePickerWindow : Window
{
    /// <summary>卡片内嵌预览视口（逻辑画布尺寸）。</summary>
    private const double PreviewWidth = 96;
    private const double PreviewHeight = 80;

    /// <summary>选中卡自动轻敲间隔（GDD-08 §3.2：每 4s 一次，恐龙沿用同参数）。</summary>
    private static readonly TimeSpan IdleTapInterval = TimeSpan.FromSeconds(4);

    private readonly StyleCatalog _styles;
    private readonly SpeciesCatalog _species;
    private readonly List<(string StyleId, StyleCatParts Parts, Border Card)> _cards = [];
    private readonly List<(string SpeciesId, Button Tab)> _tabs = [];
    private readonly DispatcherTimer _idleTapTimer;

    /// <summary>sprite 资产目录（渲染层静态注入；页签缩略图/卡片默认皮肤用，可为 null）。</summary>
    private static SpriteSpeciesCatalog? SpriteRendererSprites => StyleRenderer.SpriteSpecies;

    private string _selectedSpeciesId = SpeciesCatalog.DefaultSpeciesId;
    private string? _selectedStyleId;
    private int _idleTapSide; // 0/1 交替左右爪
    private string _lastDefaultPetName = PetNameRules.DefaultCatName;
    private bool _petNameTouched;
    private bool _suppressPetNameTouched;

    /// <summary>确认/跳过后生效的物种 id（调用方读取写入存档）。</summary>
    public string? SelectedSpeciesId => _selectedSpeciesId;

    /// <summary>确认/跳过后生效的风格 id（调用方读取写入存档）。</summary>
    public string? SelectedStyleId => _selectedStyleId;

    /// <summary>确认/跳过后生效的宠物名（已清洗；空输入回退当前物种默认名）。</summary>
    public string SelectedPetName => PetNameRules.Sanitize(
        PetNameBox.Text, PetNameRules.DefaultNameForSpecies(_selectedSpeciesId));

    public StylePickerWindow(StyleCatalog styles, SpeciesCatalog species)
    {
        InitializeComponent();
        _styles = styles;
        _species = species;

        foreach (SpeciesPack pack in species.Species)
        {
            BuildSpeciesTab(pack);
        }

        RebuildCards();

        // E9：名字输入预填物种默认名；用户主动改写后切物种不覆盖，未改写则跟随页签更新
        _lastDefaultPetName = PetNameRules.DefaultNameForSpecies(_selectedSpeciesId);
        SetPetNameBox(_lastDefaultPetName);
        PetNameBox.TextChanged += (_, _) =>
        {
            if (!_suppressPetNameTouched)
            {
                _petNameTouched = true;
            }
        };

        // 只动画选中卡（低配降级方案）：4s 一次自动轻敲，未选卡纯静态
        _idleTapTimer = new DispatcherTimer { Interval = IdleTapInterval };
        _idleTapTimer.Tick += (_, _) => PlayIdleTap();
    }

    /// <summary>程序化写名字框（不把预填当成用户编辑）。</summary>
    private void SetPetNameBox(string name)
    {
        _suppressPetNameTouched = true;
        PetNameBox.Text = name;
        PetNameBox.CaretIndex = name.Length;
        _suppressPetNameTouched = false;
    }

    /// <summary>构建一个物种页签（猫 🐱 / 恐龙 🦖 / 阿瓜 = clay 缩略图；点击切换物种，不清空风格选择）。</summary>
    private void BuildSpeciesTab(SpeciesPack pack)
    {
        object content = pack.Id switch
        {
            "cat" => (object)$"🐱 {pack.Name}",
            "dino" => $"🦖 {pack.Name}",
            _ when SpriteRendererSprites is { } sprites &&
                   SpriteArt.TryGetTabThumbnail(sprites, pack.Id, "clay") is { } thumb => BuildThumbnailTab(thumb, pack.Name),
            _ => pack.Name,
        };

        var tab = new Button
        {
            Content = content,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4, 0, 4, 0),
            Cursor = Cursors.Hand,
            Tag = pack.Id,
        };
        tab.Click += (_, _) => SelectSpecies(pack.Id);

        SpeciesTabsHost.Children.Add(tab);
        _tabs.Add((pack.Id, tab));
    }

    /// <summary>阿瓜页签内容：clay body 缩略图（12px 高，等比）+ 名称（GDD-09 §7：页签小预览）。</summary>
    private static StackPanel BuildThumbnailTab(ImageSource thumbnail, string name)
    {
        var image = new Image
        {
            Source = thumbnail,
            Height = 16,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(image);
        stack.Children.Add(text);
        return stack;
    }

    /// <summary>切换物种页签：重建 6 张风格卡预览（物种 × 风格），风格选择保持。</summary>
    private void SelectSpecies(string speciesId)
    {
        if (_selectedSpeciesId == speciesId)
        {
            return;
        }

        _selectedSpeciesId = speciesId;
        RebuildCards();

        // 名字预填跟随物种：用户没改过、清空、或仍是上一个物种默认名时更新
        string nextDefault = PetNameRules.DefaultNameForSpecies(speciesId);
        string currentName = PetNameBox.Text;
        if (!_petNameTouched || currentName.Length == 0 || currentName == _lastDefaultPetName)
        {
            SetPetNameBox(nextDefault);
            _petNameTouched = false;
        }

        _lastDefaultPetName = nextDefault;

        if (_selectedStyleId is not null)
        {
            ApplySelectionVisual();
            PlayIdleTap(); // 即时反馈：新物种 × 已选风格敲一下
        }
    }

    /// <summary>按当前物种重建全部风格卡（预览模板依赖物种形体）。</summary>
    private void RebuildCards()
    {
        CardsHost.Children.Clear();
        _cards.Clear();

        foreach (StylePack style in _styles.Styles)
        {
            BuildCard(style);
        }

        if (_selectedStyleId is not null)
        {
            ApplySelectionVisual();
        }
    }

    /// <summary>物种默认皮肤 id（选择器卡片预览按物种默认皮肤出图；无专属皮肤物种 = null）。</summary>
    private static string? DefaultSkinFor(string speciesId, SpeciesCatalog species)
        => species.TryGet(speciesId, out SpeciesPack pack) && pack.DefaultSkinId.Length > 0
            ? pack.DefaultSkinId
            : null;

    /// <summary>构建一张风格卡（当前物种 × 该风格的微型预览 + 名称；点击单选）。</summary>
    private void BuildCard(StylePack style)
    {
        var preview = new Canvas { Width = StyleArt.ArtWidth, Height = StyleArt.ArtHeight, Background = Brushes.Transparent };
        StyleCatParts parts = StyleRenderer.ApplyStyle(preview, style.Id, _styles, _selectedSpeciesId);
        string? skinId = DefaultSkinFor(_selectedSpeciesId, _species);
        StyleRenderer.ApplyOutfit(parts, style, null, skinId, null);

        var viewbox = new Viewbox
        {
            Child = preview,
            Width = PreviewWidth,
            Height = PreviewHeight,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false, // 点击落在卡片 Border 上
        };

        var title = new TextBlock
        {
            Text = style.Name,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("Text.Primary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(viewbox);
        stack.Children.Add(title);

        var card = new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(5),
            Padding = new Thickness(4),
            Background = (Brush)FindResource("Bg.Card"),
            BorderBrush = (Brush)FindResource("Border.Card"),
            BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Tag = style.Id,
        };
        card.MouseLeftButtonUp += (_, _) => Select(style.Id);

        CardsHost.Children.Add(card);
        _cards.Add((style.Id, parts, card));
    }

    /// <summary>同步页签与卡片的选中视觉（金描边 + 1.15× 放大；页签高亮）。</summary>
    private void ApplySelectionVisual()
    {
        foreach ((string id, _, Border card) in _cards)
        {
            bool selected = id == _selectedStyleId;
            card.BorderBrush = selected
                ? (Brush)FindResource("Accent.Amber") // 金色描边（琥珀主题键）
                : (Brush)FindResource("Border.Card");
            card.BorderThickness = new Thickness(selected ? 3 : 2);
        }

        foreach ((string id, Button tab) in _tabs)
        {
            bool active = id == _selectedSpeciesId;
            tab.Opacity = active ? 1.0 : 0.55;
            tab.BorderThickness = new Thickness(active ? 2 : 1);
        }
    }

    /// <summary>单选风格：放大 1.15× + 金色（琥珀）描边 + 确认按钮点亮（GDD-08 §3.2 沿用）。</summary>
    private void Select(string styleId)
    {
        _selectedStyleId = styleId;
        ApplySelectionVisual();

        foreach ((string id, _, Border card) in _cards)
        {
            if (id != styleId)
            {
                card.RenderTransform = null;
                continue;
            }

            var scale = new ScaleTransform(1.15, 1.15);
            var anim = new DoubleAnimation(1.15, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut },
            };
            card.RenderTransform = scale;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        ConfirmButton.IsEnabled = true;
        PlayIdleTap(); // 选中即敲一下（即时反馈 + 预览手感）
        if (!_idleTapTimer.IsEnabled)
        {
            _idleTapTimer.Start();
        }
    }

    /// <summary>选中卡自动轻敲（左右爪交替；未选卡不动——低配降级方案）。</summary>
    private void PlayIdleTap()
    {
        if (_selectedStyleId is null)
        {
            return;
        }

        var card = _cards.Find(c => c.StyleId == _selectedStyleId);
        if (card.Parts is null || _styles.TryGet(_selectedStyleId, out StylePack? style) is false)
        {
            return;
        }

        var side = _idleTapSide++ % 2 == 0 ? PawSide.Left : PawSide.Right;
        CatTapAnimator.PlayTap(card.Parts, style, 1.0, side);
    }

    /// <summary>确认：结果 = 所选 (species, style) 组合（存档写入由调用方完成）。</summary>
    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_selectedStyleId is null)
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    /// <summary>跳过：结果 = (cat, clay)（GDD-09 §3.1，与教程时序完全兼容）。</summary>
    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _selectedSpeciesId = SpeciesCatalog.DefaultSpeciesId;
        _selectedStyleId = StyleCatalog.DefaultStyleId;
        DialogResult = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _idleTapTimer.Stop();
        base.OnClosed(e);
    }
}
