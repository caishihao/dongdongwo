using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BongoCat.UI.Cosmetics;

/// <summary>
/// 一个风格模板实例的"命名部件集合"：装扮换绑目标（与 CosmeticArt 三套换绑 API 对接）+
/// 敲击动画 Transform 句柄。主猫（MainWindow）与远程猫（RemoteCatWindow）共用同一构建器
/// 与同一份部件契约——单一渲染数据源，杜绝两套几何漂移（GDD-08 §4）。
/// </summary>
public sealed class StyleCatParts
{
    /// <summary>风格根画布（240×200，与部件锚点坐标系一致）。</summary>
    public required Canvas Root { get; init; }

    // ── 六部件层（GDD-01 层结构：body/face/bongos/pawL/pawR/hat）────────
    public required Canvas PartBody { get; init; }
    public required Canvas PartFace { get; init; }
    public required Canvas PartBongos { get; init; }
    public required Canvas PartPawL { get; init; }
    public required Canvas PartPawR { get; init; }
    public required Canvas PartHat { get; init; }

    // ── 装扮换绑目标（CosmeticArt.ApplySkin/ApplyBongos 的绑定面；命名语义与 E3 一致）──
    public required Shape BodyFur { get; init; }
    public required Shape TailFur { get; init; }
    public required Shape EarLFur { get; init; }
    public required Shape EarRFur { get; init; }
    public required Shape EarInnerL { get; init; }
    public required Shape EarInnerR { get; init; }
    public required Shape NoseFill { get; init; }
    public required Shape PawLFur { get; init; }
    public required Shape PawRFur { get; init; }
    public required Shape PawLineL { get; init; }
    public required Shape PawLineR { get; init; }

    /// <summary>奶牛纹斑点组（skin_cow 装备时可见；容器 + 斑点形状列表供风格转译改笔法）。</summary>
    public required FrameworkElement CowSpots { get; init; }
    public required IReadOnlyList<Shape> CowSpotShapes { get; init; }

    /// <summary>
    /// 物种附属形状组（GDD-09 §2：恐龙的棘刺基座/双角基座等"随皮肤色板换绑"的形体元素，
    /// 与 Ear 同槽换色；空列表 = 猫模板无附属形状）。青绿点缀等物种固定色形状<b>不</b>在本组
    /// （固定色不随色板，GDD-09 §2.2）。构建器在 Assemble 之后赋值（非必需属性）。
    /// </summary>
    public IReadOnlyList<Shape> SpeciesAppendages { get; set; } = [];

    public required Shape BongoBodyL { get; init; }
    public required Shape BongoRimL { get; init; }
    public required Shape BongoBodyR { get; init; }
    public required Shape BongoRimR { get; init; }

    // ── 敲击动画 Transform 句柄（ADR-003 纪律：动画只动 Transform/Opacity，
    //    永不触发 Layout 重算；各风格共用同一组句柄，参数来自 StylePack）────
    public required TranslateTransform PawLTranslate { get; init; }
    public required TranslateTransform PawRTranslate { get; init; }
    public required RotateTransform PawLRotate { get; init; }
    public required RotateTransform PawRRotate { get; init; }

    /// <summary>身体挤压（以脚底为轴的 scaleY；squashScaleY=1 的风格静止不动）。</summary>
    public required ScaleTransform BodySquash { get; init; }

    /// <summary>鼓下沉（chibi 敲中鼓沉 3px）。</summary>
    public required TranslateTransform BongoSink { get; init; }

    /// <summary>hat/face 层错位反弹（剪纸"纸片拍击"；其余风格静止）。</summary>
    public required TranslateTransform HatBounce { get; init; }
    public required TranslateTransform FaceBounce { get; init; }

    /// <summary>v5 双帧切换（sprite 物种 idle/tap 整绘）；矢量物种为 null。</summary>
    public SpriteArt.ISpriteFrameSwitcher? Frames { get; init; }

    /// <summary>挤压系数覆盖（位图物种整图挤压易失真，用更小的幅度；null = 用风格参数）。</summary>
    public double? SquashOverride { get; init; }
}
