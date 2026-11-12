using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Piztki.Core;

namespace Piztki;

/// <summary>
/// Cinema: the lyrics on the whole screen. The cover, song, and controls sit on the left, the words
/// scroll large on the right, and clouds in the cover's colors drift behind everything.
/// </summary>
public partial class LyricsWindow : Window
{
    readonly NotchWindow _notch;
    readonly MediaService _media;
    readonly LyricsService _lyrics;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    GradientStop[]? _centers, _edges;
    string _columnKey = "";
    string _skyKey = "";
    bool _closing;

    public LyricsWindow(NotchWindow notch, System.Drawing.Rectangle screenPixels)
    {
        InitializeComponent();
        _notch = notch;
        _media = notch.Media;
        _lyrics = notch.Lyrics;
        Icon = App.RenderAppIcon(64);

        // cover the chosen screen exactly (sizes are in WPF units, the screen's are in pixels)
        var dpi = VisualTreeHelper.GetDpi(notch);
        Left = screenPixels.Left / dpi.DpiScaleX;
        Top = screenPixels.Top / dpi.DpiScaleY;
        Width = screenPixels.Width / dpi.DpiScaleX;
        Height = screenPixels.Height / dpi.DpiScaleY;

        // everything scales with the screen, so it looks the same on a laptop and on a big monitor
        double unit = Height / 1080.0;
        Col.LineFontSize = Math.Clamp(40 * unit, 24, 60);
        Col.LineGap = Math.Clamp(13 * unit, 8, 20);
        Cover.Width = Cover.Height = Math.Clamp(360 * unit, 220, 560);
        TitleText.MaxWidth = ArtistText.MaxWidth = Cover.Width + 120 * unit;
        Stage.Margin = new Thickness(90 * unit, 70 * unit, 90 * unit, 70 * unit);

        Col.SeekRequested += _notch.SeekToLyric;
        _timer.Tick += (_, _) => Tick();

        Loaded += (_, _) =>
        {
            LayoutSky();
            Refresh();
            _media.Changed += Refresh;
            _lyrics.Changed += Refresh;
            _timer.Start();
            StartDrift();

            Fx.Animate(Root, OpacityProperty, 1, 500);
            Fx.Appear(SongSide, dx: -30, dy: 0, ms: 600, delay: 120);
            Fx.Appear(Col, dx: 30, dy: 0, ms: 600, delay: 220);
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _media.Changed -= Refresh;
            _lyrics.Changed -= Refresh;
        };
    }

    // ----- content -----

    void Refresh()
    {
        TitleText.Text = _media.HasSession ? (_media.Title.Length > 0 ? _media.Title : "Unknown title") : "Nothing playing";
        ArtistText.Text = _media.HasSession ? _media.Artist : "Play a song in any app";
        // a Hebrew or Arabic title reads (and trims) from the right
        TitleText.FlowDirection = Controls.LyricsColumn.IsRightToLeft(TitleText.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        CoverBrush.ImageSource = _media.Art;
        CoverImage.Visibility = _media.Art != null ? Visibility.Visible : Visibility.Collapsed;
        PlayBtn.Content = _media.IsPlaying ? "" : "";

        bool wanted = App.Settings.ShowLyrics && _media.HasSession;
        bool timed = wanted && _lyrics.HasLyrics;
        bool plain = wanted && !timed && _lyrics.HasPlain;

        NoLyricsText.Text = wanted && _lyrics.Loading ? "Looking for the lyrics…" : "Lyrics not available";
        NoLyrics.Visibility = timed || plain ? Visibility.Collapsed : Visibility.Visible;
        Col.Visibility = timed || plain ? Visibility.Visible : Visibility.Collapsed;

        // hand the column its lines only when they actually change
        string key = $"{_media.Title}\n{_media.Artist}\n{timed}\n{plain}\n{_lyrics.Lines.Count}\n{_lyrics.PlainLines.Count}";
        if (key != _columnKey)
        {
            _columnKey = key;
            if (timed)
            {
                Col.SetLyrics(_lyrics.Lines, timed: true);
                Fx.Appear(Col, dx: 0, dy: 24, ms: 500);
            }
            else if (plain)
            {
                Col.SetLyrics(_lyrics.PlainLines.Select(l => (TimeSpan.Zero, l)).ToList(), timed: false);
                Fx.Appear(Col, dx: 0, dy: 24, ms: 500);
            }
            Col.Update(_notch.LyricPosition, App.Settings.KaraokeFill, force: true);
        }

        UpdateSky();
    }

    void Tick()
    {
        Col.Update(_notch.LyricPosition, App.Settings.KaraokeFill, force: false);

        var duration = _media.Duration;
        var position = _media.Position;
        PosText.Text = Format(position);
        DurText.Text = duration > TimeSpan.Zero ? Format(duration) : "";
        SeekBarS.ScaleX = duration.TotalSeconds > 1 ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1) : 0;
    }

    static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ----- sky -----

    Ellipse[] Clouds => new[] { Sky1, Sky2, Sky3, Sky4 };

    void LayoutSky()
    {
        double w = ActualWidth, h = ActualHeight;
        // (left, top, width, height) as fractions of the screen: big, overlapping, partly off the edges
        (double x, double y, double cw, double ch)[] spots =
        {
            (-0.15, -0.25, 0.75, 0.95), (0.45, -0.10, 0.75, 0.90), (0.15, 0.40, 0.70, 0.85), (0.60, 0.45, 0.60, 0.80),
        };
        var clouds = Clouds;
        for (int i = 0; i < clouds.Length; i++)
        {
            clouds[i].Width = w * spots[i].cw;
            clouds[i].Height = h * spots[i].ch;
            System.Windows.Controls.Canvas.SetLeft(clouds[i], w * spots[i].x);
            System.Windows.Controls.Canvas.SetTop(clouds[i], h * spots[i].y);
        }
    }

    void UpdateSky()
    {
        var s = App.Settings;
        var colors = s.SongColors && s.LyricsClouds && _media.HasSession ? _media.ArtColors : new List<Color>();
        // a cover with no real color still gets a calm silver-blue sky rather than plain black
        if (colors.Count == 0) colors = new List<Color> { Color.FromRgb(0x5A, 0x60, 0x78), Color.FromRgb(0x3C, 0x40, 0x58) };

        string key = string.Join(",", colors);
        if (key == _skyKey) return;
        _skyKey = key;

        var clouds = Clouds;
        if (_centers == null)
        {
            _centers = new GradientStop[clouds.Length];
            _edges = new GradientStop[clouds.Length];
            for (int i = 0; i < clouds.Length; i++)
            {
                var first = Palette.Deep(colors[i % colors.Count]);
                _centers[i] = new GradientStop(Color.FromArgb(0, first.R, first.G, first.B), 0);
                _edges[i] = new GradientStop(Color.FromArgb(0, first.R, first.G, first.B), 1);
                var brush = new RadialGradientBrush();
                brush.GradientStops.Add(_centers[i]);
                brush.GradientStops.Add(_edges[i]);
                clouds[i].Fill = brush;
            }
        }

        // a new song's colors melt into the sky over two seconds
        var time = TimeSpan.FromMilliseconds(2000);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        for (int i = 0; i < clouds.Length; i++)
        {
            var c = Palette.Deep(colors[i % colors.Count]);
            _centers[i].BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(Color.FromArgb(205, c.R, c.G, c.B), time) { EasingFunction = ease });
            _edges![i].BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(Color.FromArgb(0, c.R, c.G, c.B), time) { EasingFunction = ease });
        }
    }

    void StartDrift()
    {
        double w = ActualWidth, h = ActualHeight;
        var moves = new (TranslateTransform t, double x, double y, double seconds)[]
        {
            (Sky1T, 0.16, 0.12, 19), (Sky2T, -0.18, 0.10, 23), (Sky3T, 0.14, -0.14, 17), (Sky4T, -0.12, -0.10, 21),
        };

        foreach (var (t, x, y, seconds) in moves)
        {
            DoubleAnimation Drift(double to, double time)
            {
                var a = new DoubleAnimation(0, to, TimeSpan.FromSeconds(time))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Timeline.SetDesiredFrameRate(a, 30);
                return a;
            }
            t.BeginAnimation(TranslateTransform.XProperty, Drift(w * x, seconds));
            t.BeginAnimation(TranslateTransform.YProperty, Drift(h * y, seconds * 0.8));
        }
    }

    // ----- controls -----

    void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();
    void Play_Click(object sender, RoutedEventArgs e) => _media.PlayPause();
    void Next_Click(object sender, RoutedEventArgs e) => _media.Next();

    void Seek_Click(object sender, MouseButtonEventArgs e)
    {
        if (SeekTrack.ActualWidth <= 0 || _media.Duration <= TimeSpan.Zero) return;
        double fraction = Math.Clamp(e.GetPosition(SeekTrack).X / SeekTrack.ActualWidth, 0, 1);
        _media.Seek(TimeSpan.FromSeconds(_media.Duration.TotalSeconds * fraction));
        Col.Update(_notch.LyricPosition, App.Settings.KaraokeFill, force: true);
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CloseSmoothly();
        else if (e.Key == Key.Space) _media.PlayPause();
        else if (e.Key == Key.Right) _media.Next();
        else if (e.Key == Key.Left) _media.Previous();
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseSmoothly();

    void CloseSmoothly()
    {
        if (_closing) return;
        _closing = true;
        Fx.Animate(Root, OpacityProperty, 0, 260, Fx.EaseIn, done: Close);
    }
}
