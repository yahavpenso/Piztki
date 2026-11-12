using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Piztki.Controls;
using Piztki.Core;

namespace Piztki;

// The sing-along page inside the Music tab, the same fill on the pill's single line, the color
// clouds behind the page, and the door to the fullscreen cinema view.
public partial class NotchWindow
{
    static readonly Color Sung = Colors.White;
    static readonly Color Unsung = Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF);

    readonly DispatcherTimer _karaoke = new() { Interval = TimeSpan.FromMilliseconds(40) };
    bool _lyricsOpen;
    bool _karaokeHooked;
    bool _cloudsDrifting;
    string _cloudKey = "";
    string _columnKey = "";
    GradientStop[]? _cloudCenters, _cloudEdges;

    double _lyricPace = 0.3; // seconds per syllable, learned from the current song
    double[] _linePaces = Array.Empty<double>();

    // shared with the cinema view
    internal MediaService Media => _media;
    internal LyricsService Lyrics => _lyrics;

    /// <summary>The song position used for lyrics, including the user's fine-tuning offset.</summary>
    internal TimeSpan LyricPosition => _media.Position - TimeSpan.FromMilliseconds(_s.LyricsOffsetMs);

    /// <summary>Jumps the song to where a lyric line starts (allowing for the timing offset).</summary>
    internal void SeekToLyric(TimeSpan lineStart)
    {
        var target = lineStart + TimeSpan.FromMilliseconds(_s.LyricsOffsetMs);
        _media.Seek(target < TimeSpan.Zero ? TimeSpan.Zero : target);
        KaraokeTick(force: true);
    }

    void LyricsOpen_Click(object sender, RoutedEventArgs e) => OpenLyrics(true);
    void LyricsClose_Click(object sender, RoutedEventArgs e) => OpenLyrics(false);
    void LyricsCinema_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowCinema();

    void OpenLyrics(bool open, bool animate = true)
    {
        if (open && !_s.LyricsPage) open = false;
        _lyricsOpen = open;
        if (_ready && _s.MusicOpensOnLyrics != open) _s.MusicOpensOnLyrics = open; // come back to the same view next time

        if (!_lyricsColumnHooked)
        {
            _lyricsColumnHooked = true;
            LyricsCol.SeekRequested += SeekToLyric;
        }

        // show the page first: the lines can only be measured (and scrolled to) once it is on screen
        UpdateLyricsView(animate);
        UpdateKaraokeTimer();
    }

    bool _lyricsColumnHooked;

    /// <summary>Called when lyrics for the current song arrive (or turn out not to exist).</summary>
    void OnLyricsChanged()
    {
        _lyricPace = LyricTiming.Pace(_lyrics.Lines); // how fast this particular song is sung
        _linePaces = LyricTiming.LocalPaces(_lyrics.Lines); // and how fast each part of it is
        _miniMapIndex = -1;
        UpdateLyric(force: true);
        UpdateLyricsView();
    }

    /// <summary>Shows either the player or the lyrics page, and keeps the page's content current.</summary>
    void UpdateLyricsView(bool animate = false)
    {
        if (_lyricsFrozenForTest) return; // the QA walkthrough is showing its own made-up song

        LyricsOpenBtn.Visibility = _s.LyricsPage && _s.ShowLyrics ? Visibility.Visible : Visibility.Collapsed;

        bool show = _lyricsOpen && _s.LyricsPage && _s.ShowLyrics && _s.ShowMusic && _media.HasSession;
        bool wasShown = LyricsView.Visibility == Visibility.Visible;

        // The player stays laid out underneath so coming back is instant. Its opacity may still be
        // held by an earlier fade-in animation, which would win over a plain value, so clear that first.
        MusicOn.BeginAnimation(OpacityProperty, null);
        MusicOn.Opacity = show ? 0 : 1;
        MusicOn.IsHitTestVisible = !show;

        if (show && !wasShown)
        {
            if (animate) Fx.Appear(LyricsView, dx: 0, dy: 12, ms: 320);
            else Fx.Appear(LyricsView, dx: 0, dy: 0, ms: 1);
            LyricsView.UpdateLayout();
            _columnKey = ""; // measured sizes may have changed: rebuild the lines
        }
        else if (!show && wasShown)
        {
            LyricsView.Visibility = Visibility.Collapsed;
            if (animate) Fx.Appear(MusicOn, dx: 0, dy: -8, ms: 260);
        }
        if (!show) return;

        bool timed = _lyrics.HasLyrics;
        bool plain = !timed && _lyrics.HasPlain;

        string who = _media.Artist.Length > 0 ? $"{_media.Title} · {_media.Artist}" : _media.Title;
        LyricsTitle.Text = plain ? who + " · not timed, scroll with the wheel" : who;
        LyricsPlayBtn.Content = _media.IsPlaying ? "" : "";

        LyricsEmpty.Text = _lyrics.Loading ? "Looking for the lyrics…" : "Lyrics not available";
        LyricsEmpty.Visibility = timed || plain ? Visibility.Collapsed : Visibility.Visible;
        LyricsCol.Visibility = timed || plain ? Visibility.Visible : Visibility.Collapsed;

        // hand the column its lines only when they actually change
        string key = $"{_media.Title}\n{_media.Artist}\n{timed}\n{_lyrics.Lines.Count}\n{_lyrics.PlainLines.Count}";
        if (key != _columnKey)
        {
            _columnKey = key;
            if (timed) LyricsCol.SetLyrics(_lyrics.Lines, timed: true);
            else if (plain) LyricsCol.SetLyrics(_lyrics.PlainLines.Select(l => (TimeSpan.Zero, l)).ToList(), timed: false);
        }

        UpdateClouds();
        KaraokeTick(force: true);
    }

    // ----- following the song -----

    void UpdateKaraokeTimer()
    {
        if (!_karaokeHooked)
        {
            _karaokeHooked = true;
            _karaoke.Tick += (_, _) => KaraokeTick(force: false);
        }

        bool page = _lyricsOpen && _expanded && ReferenceEquals(_currentPanel, PanelMusic) && _media.HasSession;
        bool pill = PillShowsLyric;

        if (page != _cloudsDrifting)
        {
            _cloudsDrifting = page;
            StartCloudDrift(page);
        }

        bool needed = page || pill;
        if (needed && !_karaoke.IsEnabled)
        {
            _karaoke.Start();
            KaraokeTick(force: true);
        }
        else if (!needed && _karaoke.IsEnabled)
        {
            _karaoke.Stop();
        }

        // whenever the pill is not showing a lyric, its text goes back to plain white
        if (!pill && _miniFilling)
        {
            _miniFilling = false;
            MiniText.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    void KaraokeTick(bool force)
    {
        MiniKaraokeTick();
        if (_lyricsOpen && !_lyricsFrozenForTest && LyricsView.Visibility == Visibility.Visible)
            LyricsCol.Update(LyricPosition, _s.KaraokeFill, force);
    }

    // ----- the same fill on the single line in the collapsed pill -----

    bool _miniFilling;
    int _miniMapIndex = -1;
    LyricTiming.WordMap? _miniMap;
    GradientStop? _miniLit, _miniDark;
    LinearGradientBrush? _miniBrush;

    /// <summary>True while the collapsed pill is showing a lyric line that should fill as it is sung.</summary>
    bool PillShowsLyric =>
        !_expanded && _s.KaraokeFill && _media.IsPlaying && _miniKind.StartsWith("lyric:", StringComparison.Ordinal);

    /// <summary>When a line starts, and when its words are estimated to end.</summary>
    (TimeSpan start, TimeSpan fillEnd) LineTiming(int index)
    {
        var lines = _lyrics.Lines;
        var start = lines[index].time;
        double gap = index + 1 < lines.Count ? (lines[index + 1].time - start).TotalSeconds : 6;
        // at the pace of this part of the song, and never past the start of the next line
        if (_linePaces.Length != lines.Count) _linePaces = LyricTiming.LocalPaces(lines);
        double pace = index < _linePaces.Length ? _linePaces[index] : _lyricPace;
        return (start, start + LyricTiming.Duration(lines[index].text, gap, pace));
    }

    void MiniKaraokeTick()
    {
        if (!PillShowsLyric) return;

        var position = LyricPosition;
        int index = _lyrics.IndexAt(position);
        if (index < 0) return;

        if (index != _miniMapIndex || _miniMap == null)
        {
            _miniMapIndex = index;
            _miniMap = new LyricTiming.WordMap(_lyrics.Lines[index].text);
        }

        if (_miniBrush == null)
        {
            _miniLit = new GradientStop(Sung, 0);
            _miniDark = new GradientStop(Unsung, 0);
            _miniBrush = new LinearGradientBrush();
            _miniBrush.GradientStops.Add(_miniLit);
            _miniBrush.GradientStops.Add(_miniDark);
        }

        // the light sweeps in reading direction
        bool rtl = LyricsColumn.IsRightToLeft(MiniText.Text);
        _miniBrush.StartPoint = new Point(rtl ? 1 : 0, 0);
        _miniBrush.EndPoint = new Point(rtl ? 0 : 1, 0);

        if (!_miniFilling)
        {
            _miniFilling = true;
            MiniText.Foreground = _miniBrush;
        }

        var (start, fillEnd) = LineTiming(index);
        double span = (fillEnd - start).TotalSeconds;
        double timeProgress = span <= 0 ? 1 : Math.Clamp((position - start).TotalSeconds / span, 0, 1);
        double progress = _miniMap.CharsAt(timeProgress); // time through the line -> letters lit, word by word
        double soft = progress is <= 0 or >= 1 ? 0 : 0.04;
        _miniLit!.Offset = Math.Clamp(progress - soft, 0, 1);
        _miniDark!.Offset = Math.Clamp(progress + soft, 0, 1);
    }

    // ----- color clouds -----

    void UpdateClouds()
    {
        var colors = _s.LyricsClouds && _s.SongColors ? _media.ArtColors : new List<Color>();
        string key = string.Join(",", colors);
        if (key == _cloudKey) return;
        _cloudKey = key;

        if (colors.Count == 0)
        {
            Fx.Animate(Clouds, OpacityProperty, 0, 500);
            return;
        }

        // Each cloud keeps one brush for its whole life, and a new song's colors are blended into
        // it over a second and a half. Swapping in fresh brushes made the colors jump on every skip.
        if (_cloudCenters == null)
        {
            var shapes = new[] { Cloud1, Cloud2, Cloud3 };
            _cloudCenters = new GradientStop[shapes.Length];
            _cloudEdges = new GradientStop[shapes.Length];
            for (int i = 0; i < shapes.Length; i++)
            {
                var first = Palette.Deep(colors[i % colors.Count]);
                // start see-through but already in the right hue, so the first fade-in doesn't pass through gray
                _cloudCenters[i] = new GradientStop(Color.FromArgb(0, first.R, first.G, first.B), 0);
                _cloudEdges[i] = new GradientStop(Color.FromArgb(0, first.R, first.G, first.B), 1);
                var brush = new RadialGradientBrush();
                brush.GradientStops.Add(_cloudCenters[i]);
                brush.GradientStops.Add(_cloudEdges[i]);
                shapes[i].Fill = brush;
            }
        }

        var time = TimeSpan.FromMilliseconds(1500);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        for (int i = 0; i < _cloudCenters.Length; i++)
        {
            var c = Palette.Deep(colors[i % colors.Count]);
            _cloudCenters[i].BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(Color.FromArgb(150, c.R, c.G, c.B), time) { EasingFunction = ease });
            _cloudEdges![i].BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(Color.FromArgb(0, c.R, c.G, c.B), time) { EasingFunction = ease });
        }
        Fx.Animate(Clouds, OpacityProperty, 1, 900);
    }

    /// <summary>The clouds wander only while the lyrics page is actually on screen.</summary>
    void StartCloudDrift(bool on)
    {
        var moves = new (TranslateTransform t, double x, double y, double seconds)[]
        {
            (Cloud1T, 46, 26, 11), (Cloud2T, -52, -22, 14), (Cloud3T, 38, -30, 9),
        };

        foreach (var (t, x, y, seconds) in moves)
        {
            if (!on)
            {
                t.BeginAnimation(TranslateTransform.XProperty, null);
                t.BeginAnimation(TranslateTransform.YProperty, null);
                continue;
            }

            DoubleAnimation Drift(double to, double time)
            {
                var a = new DoubleAnimation(0, to, TimeSpan.FromSeconds(time))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Timeline.SetDesiredFrameRate(a, 15); // slow enough that 15 fps is invisible, and cheap
                return a;
            }
            t.BeginAnimation(TranslateTransform.XProperty, Drift(x, seconds));
            t.BeginAnimation(TranslateTransform.YProperty, Drift(y, seconds * 0.8));
        }
    }
}
