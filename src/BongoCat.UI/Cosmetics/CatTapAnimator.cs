using System.Windows.Media;
using System.Windows.Media.Animation;
using BongoCat.Core.Config;
using BongoCat.Core.Input;

namespace BongoCat.UI.Cosmetics;

/// <summary>
/// 风格参数化敲击动画（GDD-08 §2.6：压深/时长/缓动/挤压/爪旋转/鼓下沉/层错位全部来自
/// StylePack 数据；reboundMs 用户设置作为全局系数缩放各风格时长）。
/// ADR-003 纪律：只动 RenderTransform / Opacity，永不触发 Layout 重算；
/// 回弹期间再次按键直接打断重来（BeginAnimation 覆盖旧动画，GDD-01 §5 手感优先）。
/// 主猫与远程猫共用同一实现（同一渲染器纪律的动画侧）。
/// </summary>
public static class CatTapAnimator
{
    /// <summary>下压段固定时长（ms；与 E1 黏土手感一致，各风格共用）。</summary>
    private const int DownMs = 25;

    /// <summary>
    /// 播放一次风格化敲击。
    /// </summary>
    /// <param name="parts">风格模板部件（Transform 句柄）。</param>
    /// <param name="style">当前风格参数包。</param>
    /// <param name="reboundScale">全局回弹系数（用户设置 ReboundMs / 120 基线）。</param>
    /// <param name="side">左/右爪。</param>
    public static void PlayTap(StyleCatParts parts, StylePack style, double reboundScale, PawSide side)
    {
        bool left = side == PawSide.Left;
        var translate = left ? parts.PawLTranslate : parts.PawRTranslate;
        var rotate = left ? parts.PawLRotate : parts.PawRRotate;

        double reboundMs = style.ReboundMs * reboundScale;
        IEasingFunction ease = CreateEasing(style);

        // ① 爪：Idle → Down（线性落爪）→ Up（风格缓动回弹）
        var paw = new DoubleAnimationUsingKeyFrames();
        paw.KeyFrames.Add(new LinearDoubleKeyFrame(style.TapDownPx, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DownMs))));
        paw.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DownMs + reboundMs)))
        {
            EasingFunction = ease,
        });
        translate.BeginAnimation(TranslateTransform.YProperty, paw);

        // ② 爪旋转（sketch ±2° 手绘颤感 / chibi 4°；方向按左右爪取反）
        if (style.PawRotationDeg > 0)
        {
            double target = left ? -style.PawRotationDeg : style.PawRotationDeg;
            var rot = new DoubleAnimationUsingKeyFrames();
            rot.KeyFrames.Add(new LinearDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DownMs))));
            rot.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DownMs + reboundMs)))
            {
                EasingFunction = ease,
            });
            rotate.BeginAnimation(RotateTransform.AngleProperty, rot);
        }
        else
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        // ③ 身体挤压（以脚底为轴 scaleY：1 → squash → 1；位图物种用更小幅度防失真）
        double squashTo = parts.SquashOverride ?? style.SquashScaleY;
        if (squashTo < 1.0)
        {
            var squash = new DoubleAnimationUsingKeyFrames();
            squash.KeyFrames.Add(new LinearDoubleKeyFrame(squashTo, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DownMs))));
            squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DownMs + reboundMs)))
            {
                EasingFunction = ease,
            });
            parts.BodySquash.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
        }
        else
        {
            parts.BodySquash.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        // ③b 双帧切换（sprite 物种 v5）：敲击瞬间切 tap 帧，回弹后回 idle 帧
        if (parts.Frames is { } frames)
        {
            frames.ShowTap();
            var revert = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DownMs + reboundMs * 0.5),
            };
            revert.Tick += (_, _) =>
            {
                revert.Stop();
                frames.ShowIdle();
            };
            revert.Start();
        }

        // ④ 鼓下沉（chibi 敲中鼓沉 3px）
        if (style.BongoSinkPx > 0)
        {
            var sink = new DoubleAnimationUsingKeyFrames();
            sink.KeyFrames.Add(new LinearDoubleKeyFrame(style.BongoSinkPx, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DownMs))));
            sink.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DownMs + reboundMs)))
            {
                EasingFunction = ease,
            });
            parts.BongoSink.BeginAnimation(TranslateTransform.YProperty, sink);
        }
        else
        {
            parts.BongoSink.BeginAnimation(TranslateTransform.YProperty, null);
        }

        // ⑤ 层错位反弹（剪纸"纸片拍击"：hat/face 反向弹起后 layerBounceMs 归位）
        if (style.LayerBouncePx > 0 && style.LayerBounceMs > 0)
        {
            var bounce = new DoubleAnimationUsingKeyFrames();
            bounce.KeyFrames.Add(new LinearDoubleKeyFrame(-style.LayerBouncePx, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DownMs))));
            bounce.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DownMs + style.LayerBounceMs))));
            parts.HatBounce.BeginAnimation(TranslateTransform.YProperty, bounce);
            parts.FaceBounce.BeginAnimation(TranslateTransform.YProperty, bounce);
        }
        else
        {
            parts.HatBounce.BeginAnimation(TranslateTransform.YProperty, null);
            parts.FaceBounce.BeginAnimation(TranslateTransform.YProperty, null);
        }
    }

    /// <summary>缓动工厂：back → BackEase(Amplitude)、quadratic → QuadraticEase(EaseOut)、elastic → ElasticEase。</summary>
    private static IEasingFunction CreateEasing(StylePack style) => style.Easing switch
    {
        "quadratic" => new QuadraticEase { EasingMode = EasingMode.EaseOut },
        "elastic" => new ElasticEase
        {
            EasingMode = EasingMode.EaseOut,
            Oscillations = style.Oscillations,
            Springiness = style.Springiness,
        },
        _ => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = style.Amplitude },
    };
}
