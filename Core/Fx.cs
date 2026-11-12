using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Piztki.Core;

/// <summary>Small animation helpers shared by the notch and the settings window.</summary>
public static class Fx
{
    public static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 });

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    /// <summary>1 is normal; smaller makes every animation built here proportionally quicker.</summary>
    public static double Speed { get; set; } = 1;

    static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms * Speed);

    /// <summary>Gives the element a scale + translate pair that these helpers can animate.</summary>
    static (ScaleTransform scale, TranslateTransform move) Tx(UIElement e)
    {
        if (e.RenderTransform is TransformGroup { Children.Count: 2 } g &&
            g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
            return (s, t);

        s = new ScaleTransform(1, 1);
        t = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(s);
        group.Children.Add(t);
        e.RenderTransform = group;
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        return (s, t);
    }

    /// <summary>Fade in while sliding from an offset (and optionally growing from a smaller scale).</summary>
    public static void Appear(UIElement e, double dx = 0, double dy = 10, int ms = 320, int delay = 0, double fromScale = 1)
    {
        var (scale, move) = Tx(e);
        e.Visibility = Visibility.Visible;

        e.BeginAnimation(UIElement.OpacityProperty, Held(0, 1, delay, ms, EaseOut));
        move.BeginAnimation(TranslateTransform.XProperty, Held(dx, 0, delay, ms, EaseOut));
        move.BeginAnimation(TranslateTransform.YProperty, Held(dy, 0, delay, ms, EaseOut));

        if (fromScale != 1)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Held(fromScale, 1, delay, ms + 80, Spring));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Held(fromScale, 1, delay, ms + 80, Spring));
        }
    }

    /// <summary>
    /// Holds the start value through the delay, then eases to the end value. A plain BeginTime
    /// would show the element at its resting value until the delay ends, which reads as a flash.
    /// </summary>
    static DoubleAnimationUsingKeyFrames Held(double from, double to, int delay, int ms, IEasingFunction ease)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > 0) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(Ms(delay))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(Ms(delay + ms)), ease));
        return a;
    }

    /// <summary>Fade out while sliding toward an offset, then run <paramref name="done"/>.</summary>
    public static void Disappear(UIElement e, double dx = 0, double dy = 0, int ms = 150, Action? done = null)
    {
        var (_, move) = Tx(e);
        var fade = new DoubleAnimation(0, Ms(ms)) { EasingFunction = EaseIn };
        if (done != null) fade.Completed += (_, _) => done();

        e.BeginAnimation(UIElement.OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, Ms(ms)) { EasingFunction = EaseIn });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, Ms(ms)) { EasingFunction = EaseIn });
    }

    /// <summary>A quick springy scale-up, for things that just changed.</summary>
    public static void Pop(UIElement e, double from = 0.6, int ms = 380, int delay = 0)
    {
        var (scale, _) = Tx(e);
        var a = new DoubleAnimation(from, 1, Ms(ms)) { BeginTime = Ms(delay), EasingFunction = Spring };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    public static void ScaleTo(UIElement e, double to, int ms, IEasingFunction? ease = null)
    {
        var (scale, _) = Tx(e);
        var a = new DoubleAnimation(to, Ms(ms)) { EasingFunction = ease ?? EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    public static void Animate(IAnimatable target, DependencyProperty property, double to, int ms, IEasingFunction? ease = null, int delay = 0, Action? done = null)
    {
        var a = new DoubleAnimation(to, Ms(ms)) { EasingFunction = ease ?? EaseOut, BeginTime = Ms(delay) };
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(property, a);
    }

    // ----- Fx.Press: buttons squish a little while held -----

    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnPressChanged));

    public static bool GetPress(DependencyObject d) => (bool)d.GetValue(PressProperty);
    public static void SetPress(DependencyObject d, bool value) => d.SetValue(PressProperty, value);

    static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el || e.NewValue is not true) return;
        el.PreviewMouseLeftButtonDown += (_, _) => ScaleTo(el, 0.9, 90);
        el.PreviewMouseLeftButtonUp += (_, _) => ScaleTo(el, 1, 320, Spring);
        el.MouseLeave += (_, _) => ScaleTo(el, 1, 180);
    }
}
