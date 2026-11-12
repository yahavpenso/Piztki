using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Piztki.Controls;

public enum MascotMood { Normal, Happy, Sleepy, Sad }

/// <summary>Piztki: a little night-sky cube with floating hands.</summary>
public partial class Mascot : UserControl
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(Mascot),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xAF, 0xA9, 0xEC)), (d, _) => ((Mascot)d).ApplyAccent()));

    public static readonly DependencyProperty TextureProperty = DependencyProperty.Register(
        nameof(Texture), typeof(string), typeof(Mascot),
        new PropertyMetadata("Stars", (d, _) => ((Mascot)d).BuildTexture()));

    public static readonly DependencyProperty HatProperty = DependencyProperty.Register(
        nameof(Hat), typeof(string), typeof(Mascot),
        new PropertyMetadata("None", (d, _) => ((Mascot)d).BuildHat()));

    static readonly Color Lilac = Color.FromRgb(0xCE, 0xCB, 0xF6);
    static readonly Color Rose = Color.FromRgb(0xF4, 0xC0, 0xD1);

    readonly DispatcherTimer _blink = new();
    readonly Random _rng = new();
    readonly List<UIElement> _twinklers = new();
    MascotMood _mood = MascotMood.Normal;
    bool _dancing;
    bool _running;
    bool _busy;
    double _lookX, _lookY;

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>Stars, Carbon, Waves, Honeycomb, or Plain.</summary>
    public string Texture
    {
        get => (string)GetValue(TextureProperty);
        set => SetValue(TextureProperty, value);
    }

    /// <summary>None, Party, Crown, Beanie, or Bow.</summary>
    public string Hat
    {
        get => (string)GetValue(HatProperty);
        set => SetValue(HatProperty, value);
    }

    /// <summary>
    /// Set on the tiny mascot in the collapsed pill. At that size 30 fps is indistinguishable from 60,
    /// and it is the one that is on screen all day, so it gets the cheaper animation.
    /// </summary>
    public bool Small { get; set; }

    public bool Dancing
    {
        get => _dancing;
        set
        {
            if (_dancing == value) return;
            _dancing = value;
            if (_running && !_busy) StartIdle();
        }
    }

    public MascotMood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            _mood = value;
            ApplyMood();
        }
    }

    public Mascot()
    {
        InitializeComponent();
        Stars.Clip = new RectangleGeometry(new Rect(1.5, 1.5, 53, 53), 14.5, 14.5);
        BuildTexture();
        BuildHat();
        ApplyAccent();

        _blink.Tick += (_, _) => Blink();
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
    }

    void ApplyAccent()
    {
        if (BodyShape == null) return;
        BodyShape.BorderBrush = Accent;
        HandLShape.Stroke = Accent;
        HandRShape.Stroke = Accent;
        BuildHat(); // some hats take the glow color
    }

    // ---------- texture ----------

    void BuildTexture()
    {
        if (Stars == null) return;
        foreach (var t in _twinklers) t.BeginAnimation(OpacityProperty, null);
        _twinklers.Clear();
        Stars.Children.Clear();

        switch (Texture)
        {
            case "Carbon": BuildCarbon(); break;
            case "Waves": BuildWaves(); break;
            case "Honeycomb": BuildHoneycomb(); break;
            case "Plain": break;
            default: BuildStars(); break;
        }

        if (_running) StartTwinkle();
    }

    void BuildStars()
    {
        // x, y, radius, colour index — hand-placed so the face stays clear
        (double x, double y, double r, int c)[] dots =
        {
            (8, 12, 0.9, 0), (20, 7, 0.7, 0), (47, 22, 0.8, 1), (9, 31, 0.7, 2), (6, 45, 0.9, 0),
            (27, 48, 0.8, 1), (48, 36, 0.7, 0), (36, 9, 0.6, 2), (17, 43, 0.6, 0), (41, 47, 0.7, 2),
        };
        Color[] colors = { Colors.White, Lilac, Rose };

        foreach (var (x, y, r, c) in dots)
        {
            var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = new SolidColorBrush(colors[c]), Opacity = 0.65 };
            Canvas.SetLeft(e, x - r);
            Canvas.SetTop(e, y - r);
            Stars.Children.Add(e);
            _twinklers.Add(e);
        }

        AddSparkle(44, 11, 3.6, Colors.White);
        AddSparkle(11, 20, 2.4, Lilac);
    }

    void AddSparkle(double cx, double cy, double size, Color color)
    {
        double s = size, t = size * 0.24;
        var geo = Geometry.Parse(FormattableString.Invariant(
            $"M 0,{-s} L {t},{-t} L {s},0 L {t},{t} L 0,{s} L {-t},{t} L {-s},0 L {-t},{-t} Z"));
        var p = new Path { Data = geo, Fill = new SolidColorBrush(color), Opacity = 0.9 };
        Canvas.SetLeft(p, cx);
        Canvas.SetTop(p, cy);
        Stars.Children.Add(p);
        _twinklers.Add(p);
    }

    void BuildCarbon()
    {
        // a woven checker: alternating squares catch the light differently
        var group = new GeometryGroup();
        for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
                if ((row + col) % 2 == 0)
                    group.Children.Add(new RectangleGeometry(new Rect(col * 7, row * 7, 7, 7)));
        Stars.Children.Add(new Path { Data = group, Fill = Brushes.White, Opacity = 0.085 });
    }

    void BuildWaves()
    {
        var sb = new StringBuilder();
        for (double y = 6; y < 56; y += 7)
        {
            sb.Append(CultureInfo.InvariantCulture, $"M -2,{y} Q 1.5,{y - 3} 5,{y} ");
            for (double x = 12; x <= 61; x += 7) sb.Append(CultureInfo.InvariantCulture, $"T {x},{y} ");
        }
        Stars.Children.Add(new Path
        {
            Data = Geometry.Parse(sb.ToString()),
            Stroke = new SolidColorBrush(Color.FromRgb(0x85, 0xB7, 0xEB)),
            StrokeThickness = 1,
            Opacity = 0.3,
        });
    }

    void BuildHoneycomb()
    {
        const double r = 6.2;
        double w = Math.Sqrt(3) * r;
        var sb = new StringBuilder();
        for (int row = -1; row < 7; row++)
        {
            for (int col = -1; col < 7; col++)
            {
                double cx = col * w + (row % 2 == 0 ? 0 : w / 2), cy = row * r * 1.5;
                for (int i = 0; i < 6; i++)
                {
                    double a = Math.PI / 180 * (60 * i - 30);
                    sb.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? "M" : "L")} {cx + r * Math.Cos(a):0.##},{cy + r * Math.Sin(a):0.##} ");
                }
                sb.Append("Z ");
            }
        }
        Stars.Children.Add(new Path
        {
            Data = Geometry.Parse(sb.ToString()),
            Stroke = new SolidColorBrush(Color.FromRgb(0xFA, 0xC7, 0x75)),
            StrokeThickness = 0.8,
            Opacity = 0.28,
        });
    }

    // ---------- hats ----------

    void BuildHat()
    {
        if (HatLayer == null) return;
        HatLayer.Children.Clear();

        var gold = new SolidColorBrush(Color.FromRgb(0xFA, 0xC7, 0x75));
        var pink = new SolidColorBrush(Color.FromRgb(0xED, 0x93, 0xB1));
        var dark = new SolidColorBrush(Color.FromRgb(0x1B, 0x18, 0x30));

        void Add(string data, Brush fill, Brush? stroke = null) => HatLayer.Children.Add(new Path
        {
            Data = Geometry.Parse(data),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = stroke == null ? 0 : 1.2,
            StrokeLineJoin = PenLineJoin.Round,
        });

        switch (Hat)
        {
            case "Party":
                Add("M 28,-15 L 39,3 L 17,3 Z", Accent);
                Add("M 23.5,-6 L 33,-6 L 35.5,-2 L 21,-2 Z", Brushes.White);
                Add("M 28,-18.5 A 3,3 0 1 1 27.99,-18.5 Z", gold);
                break;
            case "Crown":
                Add("M 15,3 L 15,-9 L 22,-2 L 28,-12 L 34,-2 L 41,-9 L 41,3 Z", gold);
                Add("M 28,-3.5 A 1.8,1.8 0 1 1 27.99,-3.5 Z", pink);
                break;
            case "Beanie":
                Add("M 11,4 A 17,15 0 0 1 45,4 Z", Accent);
                Add("M 9.5,1 L 46.5,1 L 46.5,6 L 9.5,6 Z", Brushes.White);
                Add("M 28,-16 A 3.6,3.6 0 1 1 27.99,-16 Z", Brushes.White);
                break;
            case "Bow":
                Add("M 40,2 L 30,-5 L 30,9 Z", pink, dark);
                Add("M 40,2 L 50,-5 L 50,9 Z", pink, dark);
                Add("M 40,-1.5 A 3.5,3.5 0 1 1 39.99,-1.5 Z", pink, dark);
                break;
        }
    }

    // ---------- moods ----------

    void ApplyMood()
    {
        Mouth.Data = Geometry.Parse(_mood switch
        {
            MascotMood.Happy => "M 21,36 Q 28,44.5 35,36",
            MascotMood.Sad => "M 23,40.5 Q 28,36 33,40.5",
            MascotMood.Sleepy => "M 25,38.5 L 31,38.5",
            _ => "M 23,37 Q 28,41.5 33,37",
        });

        // sleepy eyes rest half shut and stop blinking
        double eye = _mood == MascotMood.Sleepy ? 0.32 : 1;
        var close = new DoubleAnimation(eye, TimeSpan.FromMilliseconds(260));
        EyeLS.BeginAnimation(ScaleTransform.ScaleYProperty, close);
        EyeRS.BeginAnimation(ScaleTransform.ScaleYProperty, close);

        if (_mood == MascotMood.Sleepy && _running)
        {
            var drift = new DoubleAnimation(0, 0.85, TimeSpan.FromSeconds(1.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(drift, 20);
            Zzz.BeginAnimation(OpacityProperty, drift);
        }
        else
        {
            Zzz.BeginAnimation(OpacityProperty, null);
        }
    }

    // ---------- animation ----------

    void Start()
    {
        if (_running) return;
        _running = true;
        _busy = false;
        StartIdle();
        StartTwinkle();
        ApplyMood();
        ScheduleBlink();
    }

    void StartTwinkle()
    {
        // Every running animation is a clock WPF has to tick, so the small mascot twinkles only
        // every third star (you can't tell at 40 pixels) and all of them at a slow frame rate.
        int step = Small ? 3 : 1;
        for (int i = 0; i < _twinklers.Count; i += step)
        {
            var a = new DoubleAnimation(0.25, 0.95, TimeSpan.FromSeconds(0.9 + _rng.NextDouble() * 1.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(_rng.NextDouble() * 2),
            };
            Timeline.SetDesiredFrameRate(a, 15);
            _twinklers[i].BeginAnimation(OpacityProperty, a);
        }
    }

    void Stop()
    {
        _running = false;
        _blink.Stop();
        BobT.BeginAnimation(TranslateTransform.YProperty, null);
        BodyR.BeginAnimation(RotateTransform.AngleProperty, null);
        HandLT.BeginAnimation(TranslateTransform.YProperty, null);
        HandRT.BeginAnimation(TranslateTransform.YProperty, null);
        HandRT.BeginAnimation(TranslateTransform.XProperty, null);
        Zzz.BeginAnimation(OpacityProperty, null);
        foreach (var star in _twinklers) star.BeginAnimation(OpacityProperty, null);
    }

    static DoubleAnimation Loop(double from, double to, double seconds, double delay = 0, int fps = 60)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(a, fps);
        return a;
    }

    // ----- moving on the music's own beat -----

    bool _onBeat;
    int _beatNumber;
    DispatcherTimer? _beatLost;

    /// <summary>
    /// Call on every beat of the playing song: the mascot dips and bounces once, leaning to the
    /// other side each time, timed to land back just as the next beat arrives. If the beats stop
    /// coming it falls back to its own steady dance.
    /// </summary>
    public void Beat(double seconds)
    {
        if (!_running || _busy || !_dancing) return;
        seconds = Math.Clamp(seconds, 0.3, 0.9);
        _onBeat = true;
        _beatNumber++;
        bool left = _beatNumber % 2 == 0;
        int fps = Small ? 30 : 60;

        DoubleAnimationUsingKeyFrames Path(params (double value, double at)[] points)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(seconds) };
            foreach (var (value, at) in points)
                a.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromPercent(at), new SineEase { EasingMode = EasingMode.EaseInOut }));
            Timeline.SetDesiredFrameRate(a, fps);
            return a;
        }

        // a quick dip right on the beat, a hop, and back down in time for the next one
        BobT.BeginAnimation(TranslateTransform.YProperty, Path((2.6, 0.14), (-4.2, 0.55), (0, 1)));
        // lean the other way each beat
        var lean = new DoubleAnimation(left ? -8 : 8, TimeSpan.FromSeconds(seconds * 0.85)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Timeline.SetDesiredFrameRate(lean, fps);
        BodyR.BeginAnimation(RotateTransform.AngleProperty, lean);
        // one hand punches up on each beat, the other settles
        HandLT.BeginAnimation(TranslateTransform.YProperty, left ? Path((3, 0.12), (-11, 0.5), (-2, 1)) : Path((2, 0.5), (1, 1)));
        HandRT.BeginAnimation(TranslateTransform.YProperty, left ? Path((2, 0.5), (1, 1)) : Path((3, 0.12), (-11, 0.5), (-2, 1)));

        _beatLost ??= CreateBeatLost();
        _beatLost.Stop();
        _beatLost.Interval = TimeSpan.FromSeconds(seconds * 2.5);
        _beatLost.Start();
    }

    DispatcherTimer CreateBeatLost()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _onBeat = false;
            if (_running && !_busy) StartIdle(); // no beat for a while: the steady dance (or the idle float) takes over
        };
        return timer;
    }

    void StartIdle()
    {
        _busy = false;
        if (_dancing && _onBeat) return; // the next Beat() call carries on the dance
        HandRT.BeginAnimation(TranslateTransform.XProperty, null);

        if (_dancing)
        {
            int fps = Small ? 30 : 60;
            BobT.BeginAnimation(TranslateTransform.YProperty, Loop(0, -3, 0.28, 0, fps));
            BodyR.BeginAnimation(RotateTransform.AngleProperty, Loop(-7, 7, 0.56, 0, fps));
            HandLT.BeginAnimation(TranslateTransform.YProperty, Loop(3, -10, 0.28, 0, fps));
            HandRT.BeginAnimation(TranslateTransform.YProperty, Loop(-10, 3, 0.28, 0, fps));
        }
        else
        {
            // the slow idle float looks the same at 30 fps and costs half as much
            BobT.BeginAnimation(TranslateTransform.YProperty, Loop(0, -2, 1.7, 0, 30));
            BodyR.BeginAnimation(RotateTransform.AngleProperty, null);
            HandLT.BeginAnimation(TranslateTransform.YProperty, Loop(0, -3, 1.7, 0.25, 30));
            HandRT.BeginAnimation(TranslateTransform.YProperty, Loop(0, -3, 1.7, 0.6, 30));
        }
    }

    void ScheduleBlink()
    {
        _blink.Interval = TimeSpan.FromSeconds(2.4 + _rng.NextDouble() * 3.6);
        _blink.Start();
    }

    void Blink()
    {
        _blink.Stop();
        if (!_running) return;
        if (_mood != MascotMood.Sleepy)
        {
            var a = new DoubleAnimation(1, 0.12, TimeSpan.FromMilliseconds(75)) { AutoReverse = true };
            EyeLS.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            EyeRS.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        ScheduleBlink();
    }

    /// <summary>Nudge the eyes toward a direction. x and y are -1..1.</summary>
    public void LookAt(double x, double y)
    {
        x = Math.Clamp(x, -1, 1) * 3.2;
        y = Math.Clamp(y, -1, 1) * 2.4;
        if (Math.Abs(x - _lookX) < 0.12 && Math.Abs(y - _lookY) < 0.12) return;
        _lookX = x;
        _lookY = y;

        // glide instead of snapping so the eyes never jitter
        var time = TimeSpan.FromMilliseconds(160);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        FaceT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, time) { EasingFunction = ease });
        FaceT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, time) { EasingFunction = ease });
    }

    /// <summary>Both hands pump in the air: a task was finished, a timer ended.</summary>
    public void Celebrate()
    {
        if (!_running) return;
        _busy = true;

        DoubleAnimation Pump() => new(0, -15, TimeSpan.FromMilliseconds(170))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        var left = Pump();
        left.Completed += (_, _) => { if (_running) StartIdle(); };
        HandLT.BeginAnimation(TranslateTransform.YProperty, left);
        HandRT.BeginAnimation(TranslateTransform.YProperty, Pump());

        var hop = Pump();
        hop.To = -5;
        BobT.BeginAnimation(TranslateTransform.YProperty, hop);
    }

    /// <summary>The right hand waves hello.</summary>
    public void Wave()
    {
        if (!_running || _busy) return;
        _busy = true;

        HandRT.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-17, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });

        var wiggle = new DoubleAnimation(-3, 4, TimeSpan.FromMilliseconds(130))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
            BeginTime = TimeSpan.FromMilliseconds(140),
        };
        wiggle.Completed += (_, _) => { if (_running) StartIdle(); };
        HandRT.BeginAnimation(TranslateTransform.XProperty, wiggle);
    }

    /// <summary>A happy hop with a full spin, for when Piztki gets poked.</summary>
    public void Jump()
    {
        if (!_running || _busy) return;
        _busy = true;

        DoubleAnimation Hop(double height) => new(0, height, TimeSpan.FromMilliseconds(230))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BobT.BeginAnimation(TranslateTransform.YProperty, Hop(-13));
        HandLT.BeginAnimation(TranslateTransform.YProperty, Hop(-12));
        HandRT.BeginAnimation(TranslateTransform.YProperty, Hop(-12));

        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        spin.Completed += (_, _) => { if (_running) StartIdle(); };
        BodyR.BeginAnimation(RotateTransform.AngleProperty, spin);
    }
}
