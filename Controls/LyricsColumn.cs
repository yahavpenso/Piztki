using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Piztki.Core;

namespace Piztki.Controls;

/// <summary>
/// A scrolling column of lyrics that follows a song: the current line grows, brightens, and fills
/// with light as it is sung; instrumental gaps show three dots filling up; clicking a line asks to
/// jump there; the mouse wheel scrolls by hand. Used small in the notch and large in the cinema view.
/// </summary>
public sealed class LyricsColumn : Grid
{
    static readonly Color Sung = Colors.White;
    static readonly Color Unsung = Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF);

    /// <summary>One entry on screen: a lyric line (possibly wrapped over several rows) or an interlude.</summary>
    sealed class Row
    {
        public required FrameworkElement Panel;
        public required ScaleTransform Scale;
        public required TimeSpan Start;
        public required TimeSpan FillEnd;
        public int Characters = 1;
        public LyricTiming.WordMap? Map;
        public List<(GradientStop lit, GradientStop dark, int from, int length)> Parts = new();
        public Ellipse[]? Dots; // set for an interlude
    }

    readonly Canvas _canvas = new();
    readonly StackPanel _stack = new();
    readonly TranslateTransform _scroll = new();
    readonly List<Row> _rows = new();

    IReadOnlyList<(TimeSpan time, string text)> _lines = Array.Empty<(TimeSpan, string)>();
    bool _timed = true;
    int _index = -2;
    double _builtForWidth;
    DateTime _handScrollUntil = DateTime.MinValue;

    /// <summary>Text size of the lines. Set before lyrics are given.</summary>
    public double LineFontSize { get; set; } = 15;

    /// <summary>Space above and below each line.</summary>
    public double LineGap { get; set; } = 5;

    /// <summary>Where the current line sits in the visible area: 0 is the top, 0.5 the middle.</summary>
    public double FocusAt { get; set; } = 0.4;

    /// <summary>Raised when the user clicks a line, with the moment that line starts.</summary>
    public event Action<TimeSpan>? SeekRequested;

    public bool HasRows => _rows.Count > 0;

    // read-only peeks for the QA walkthrough
    internal double ScrollOffset => _scroll.Y;
    internal int RowCount => _rows.Count;
    internal FrameworkElement RowPanel(int index) => _rows[index].Panel;
    internal double RowOpacity(int index) => _rows[index].Panel.Opacity;

    public LyricsColumn()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent; // so the wheel works anywhere over the column

        // lines fade out toward the top and bottom edges
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.18));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.8));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        mask.Freeze();
        OpacityMask = mask;

        // A Canvas, because any other panel would clip the tall column to the small visible area
        // before it is slid into place, leaving nothing to see.
        _stack.RenderTransform = _scroll;
        _canvas.Children.Add(_stack);
        Children.Add(_canvas);

        MouseWheel += OnWheel;
        SizeChanged += (_, e) =>
        {
            // a different width means the lines wrap differently
            if (_lines.Count > 0 && Math.Abs(e.NewSize.Width - _builtForWidth) > 12) Build();
        };
    }

    /// <summary>
    /// Shows these lines. <paramref name="timed"/> false means plain text with no timing: nothing
    /// is highlighted and the user scrolls by hand.
    /// </summary>
    public void SetLyrics(IReadOnlyList<(TimeSpan time, string text)> lines, bool timed)
    {
        _lines = lines;
        _timed = timed;
        Build();
    }

    void Build()
    {
        _stack.Children.Clear();
        _rows.Clear();
        _index = -2;
        _scroll.BeginAnimation(TranslateTransform.YProperty, null);
        _scroll.Y = 0;
        _handScrollUntil = DateTime.MinValue;

        double width = ActualWidth > 40 ? ActualWidth : 320;
        _builtForWidth = width;
        _stack.Width = width;
        if (_lines.Count == 0) return;

        var typeface = new Typeface(System.Windows.Documents.TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double wrapAt = width - LineFontSize * 0.6;
        var paces = _timed ? LyricTiming.LocalPaces(_lines) : Array.Empty<double>();

        // a song that starts with music gets dots first, counting down to the first line
        if (_timed && _lines[0].time > TimeSpan.FromSeconds(4))
            AddInterlude(TimeSpan.Zero, _lines[0].time);

        for (int i = 0; i < _lines.Count; i++)
        {
            string text = _lines[i].text;
            var start = _lines[i].time;
            double gap = i + 1 < _lines.Count ? (_lines[i + 1].time - start).TotalSeconds : 6;

            if (_timed && text.Length == 0)
            {
                AddInterlude(start, start + TimeSpan.FromSeconds(gap)); // the lyrics mark this as a pause
                continue;
            }

            var fillEnd = _timed ? start + LyricTiming.Duration(text, gap, paces[i]) : start;
            AddLine(text, start, fillEnd, typeface, dip, wrapAt);

            // a long stretch of music after the words end also gets dots
            if (_timed && i + 1 < _lines.Count && (_lines[i + 1].time - fillEnd).TotalSeconds > 6)
                AddInterlude(fillEnd + TimeSpan.FromSeconds(0.6), _lines[i + 1].time);
        }

        _stack.UpdateLayout(); // positions are needed straight away to scroll to the current line
    }

    void AddLine(string text, TimeSpan start, TimeSpan fillEnd, Typeface typeface, double dip, double wrapAt)
    {
        bool rtl = IsRightToLeft(text);
        var scale = new ScaleTransform(_timed ? 0.94 : 1, _timed ? 0.94 : 1);
        var panel = new StackPanel
        {
            Margin = new Thickness(0, LineGap, 0, LineGap),
            Opacity = _timed ? 0.5 : 0.92,
            Background = Brushes.Transparent, // clickable across the full width
            RenderTransform = scale,
            RenderTransformOrigin = new Point(rtl ? 1 : 0, 0.5), // grows away from the edge the text starts at
        };
        var row = new Row
        {
            Panel = panel,
            Scale = scale,
            Start = start,
            FillEnd = fillEnd,
            Characters = Math.Max(1, text.Length),
            Map = new LyricTiming.WordMap(text),
        };

        int consumed = 0;
        foreach (string piece in Wrap(text, typeface, dip, wrapAt))
        {
            var lit = new GradientStop(Sung, _timed ? 0 : 1);
            var dark = new GradientStop(_timed ? Unsung : Sung, _timed ? 0 : 1);
            var brush = new LinearGradientBrush
            {
                // the light sweeps in reading direction
                StartPoint = new Point(rtl ? 1 : 0, 0),
                EndPoint = new Point(rtl ? 0 : 1, 0),
            };
            brush.GradientStops.Add(lit);
            brush.GradientStops.Add(dark);

            panel.Children.Add(new TextBlock
            {
                Text = piece,
                FontSize = LineFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = brush,
                HorizontalAlignment = rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            });
            row.Parts.Add((lit, dark, consumed, Math.Max(1, piece.Length)));
            consumed += piece.Length + 1;
        }

        if (_timed)
        {
            panel.Cursor = Cursors.Hand;
            panel.ToolTip = "Jump to this line";
            panel.MouseLeftButtonUp += (_, _) => SeekRequested?.Invoke(start);
        }

        _stack.Children.Add(panel);
        _rows.Add(row);
    }

    /// <summary>Three dots for a stretch with no words. They fill one after another as it passes.</summary>
    void AddInterlude(TimeSpan start, TimeSpan end)
    {
        double size = LineFontSize * 0.5;
        var dots = new Ellipse[3];
        var line = new StackPanel { Orientation = Orientation.Horizontal, Height = LineFontSize * 1.3 };
        for (int i = 0; i < 3; i++)
        {
            dots[i] = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = Brushes.White,
                Opacity = 0.3,
                Margin = new Thickness(0, 0, size * 0.7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            line.Children.Add(dots[i]);
        }

        var scale = new ScaleTransform(0.94, 0.94);
        var panel = new Border
        {
            Child = line,
            Margin = new Thickness(0, LineGap, 0, LineGap),
            Opacity = 0.5,
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0, 0.5),
        };

        _stack.Children.Add(panel);
        _rows.Add(new Row { Panel = panel, Scale = scale, Start = start, FillEnd = end, Dots = dots });
    }

    /// <summary>Breaks a line into rows that fit the column, on word boundaries.</summary>
    List<string> Wrap(string text, Typeface typeface, double dip, double maxWidth)
    {
        double Width(string s) => new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, LineFontSize, Brushes.White, dip).WidthIncludingTrailingWhitespace;

        var rows = new List<string>();
        string current = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string attempt = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && Width(attempt) > maxWidth)
            {
                rows.Add(current);
                current = word;
            }
            else
            {
                current = attempt;
            }
        }
        if (current.Length > 0) rows.Add(current);
        if (rows.Count == 0) rows.Add(text);
        return rows;
    }

    public static bool IsRightToLeft(string text)
    {
        foreach (char c in text)
        {
            if (c is >= '֐' and <= 'ࣿ') return true; // Hebrew, Arabic and neighbours
            if (char.IsLetter(c)) return false;
        }
        return false;
    }

    // ----- following the song -----

    int IndexAt(TimeSpan position)
    {
        int index = -1;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Start > position) break;
            index = i;
        }
        return index;
    }

    /// <summary>
    /// Moves the column to match the song. Call it often (about 25 times a second) while it is on
    /// screen. <paramref name="force"/> redraws everything at once, for after a seek or a rebuild.
    /// </summary>
    public void Update(TimeSpan position, bool fillAsSung, bool force)
    {
        if (!_timed || _rows.Count == 0) return;

        int index = IndexAt(position);
        if (index != _index || force)
        {
            int previous = _index;
            _index = index;

            for (int i = 0; i < _rows.Count; i++)
            {
                bool current = i == index;
                var row = _rows[i];
                double opacity = current ? 1 : i < index ? 0.3 : 0.5; // lines already sung sit further back
                double scale = current ? 1 : 0.94;

                if (force)
                {
                    row.Panel.BeginAnimation(OpacityProperty, null);
                    row.Panel.Opacity = opacity;
                    row.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    row.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    row.Scale.ScaleX = row.Scale.ScaleY = scale;
                }
                else if (i == index || i == previous)
                {
                    // the line being sung swells up; the one just finished settles back
                    Fx.Animate(row.Panel, OpacityProperty, opacity, 320);
                    Fx.Animate(row.Scale, ScaleTransform.ScaleXProperty, scale, current ? 420 : 300, current ? Fx.Spring : Fx.EaseOut);
                    Fx.Animate(row.Scale, ScaleTransform.ScaleYProperty, scale, current ? 420 : 300, current ? Fx.Spring : Fx.EaseOut);
                }

                if (!current) SetFill(row, 0);
            }

            if (DateTime.Now >= _handScrollUntil) ScrollTo(Math.Max(index, 0), animate: !force);
        }

        if (index >= 0)
        {
            var row = _rows[index];
            double span = (row.FillEnd - row.Start).TotalSeconds;
            double progress = span <= 0 ? 1 : Math.Clamp((position - row.Start).TotalSeconds / span, 0, 1);
            SetFill(row, fillAsSung || row.Dots != null ? progress : 1);
        }
    }

    static void SetFill(Row row, double progress)
    {
        if (row.Dots is { } dots)
        {
            // each dot brightens and swells during its third of the pause
            for (int i = 0; i < dots.Length; i++)
            {
                double part = Math.Clamp(progress * dots.Length - i, 0, 1);
                dots[i].Opacity = 0.3 + 0.7 * part;
                var scale = (ScaleTransform)dots[i].RenderTransform;
                scale.ScaleX = scale.ScaleY = 0.8 + 0.4 * part;
            }
            return;
        }

        // time through the line -> letters lit: longer words take longer, the last word is held
        double reached = (row.Map?.CharsAt(progress) ?? progress) * row.Characters;
        foreach (var (lit, dark, from, length) in row.Parts)
        {
            double part = Math.Clamp((reached - from) / length, 0, 1);
            // a short soft edge between sung and unsung, collapsing to nothing at either end
            double soft = part is <= 0 or >= 1 ? 0 : 0.04;
            lit.Offset = Math.Clamp(part - soft, 0, 1);
            dark.Offset = Math.Clamp(part + soft, 0, 1);
        }
    }

    void ScrollTo(int index, bool animate)
    {
        if (index >= _rows.Count) return;
        var panel = _rows[index].Panel;
        if (panel.ActualHeight <= 0 || ActualHeight <= 0) return;

        double top = panel.TranslatePoint(new Point(0, 0), _stack).Y;
        double target = Math.Min(ActualHeight * FocusAt - (top + panel.ActualHeight / 2), LineFontSize * 0.4);

        if (animate)
        {
            // a soft overshoot gives the column a little weight as it moves
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
            Fx.Animate(_scroll, TranslateTransform.YProperty, target, 560, ease);
        }
        else
        {
            _scroll.BeginAnimation(TranslateTransform.YProperty, null);
            _scroll.Y = target;
        }
    }

    /// <summary>The wheel scrolls by hand; a few seconds later the column goes back to following the song.</summary>
    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_rows.Count == 0) return;
        e.Handled = true;

        double current = _scroll.Y; // includes any animation in flight
        _scroll.BeginAnimation(TranslateTransform.YProperty, null);

        double lowest = Math.Min(0, ActualHeight * 0.6 - _stack.ActualHeight);
        _scroll.Y = Math.Clamp(current + e.Delta * 0.5, lowest, LineFontSize * 0.4);

        _handScrollUntil = DateTime.Now.AddSeconds(_timed ? 4 : 3600);
        if (_timed) _index = -2; // so it snaps back to the right line when the pause ends
    }
}
