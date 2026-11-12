using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Piztki;

/// <summary>
/// The color picker: a frozen picture of the desktop with a magnifying glass that follows the
/// pointer. Clicking takes the color of the pixel in the middle of the glass.
/// </summary>
public sealed class ColorPickWindow : Window
{
    const int Around = 5;       // pixels shown on each side of the middle one
    const double Cell = 10;     // how big each of them is drawn

    readonly BitmapSource _shot;
    readonly Border _glass;
    readonly Image _zoom;
    readonly Border _tag;
    readonly TextBlock _tagText;
    readonly Border _tagSwatch;

    public Color? Picked { get; private set; }

    public ColorPickWindow(BitmapSource shot)
    {
        _shot = shot;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var root = new Grid();
        root.Children.Add(new Image { Source = shot, Stretch = Stretch.Fill });

        var canvas = new Canvas();
        double size = (Around * 2 + 1) * Cell;

        _zoom = new Image { Width = size, Height = size, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_zoom, BitmapScalingMode.NearestNeighbor);
        var lens = new Grid { Width = size, Height = size, Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2) };
        lens.Children.Add(_zoom);
        // a small frame around the pixel that will be taken
        lens.Children.Add(new Rectangle { Width = Cell + 2, Height = Cell + 2, Stroke = Brushes.White, StrokeThickness = 1.5 });
        lens.Children.Add(new Rectangle { Width = Cell + 4, Height = Cell + 4, Stroke = Brushes.Black, StrokeThickness = 1 });

        _glass = new Border
        {
            Width = size + 6,
            Height = size + 6,
            CornerRadius = new CornerRadius((size + 6) / 2),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(3),
            Background = Brushes.Black,
            Child = lens,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        canvas.Children.Add(_glass);

        _tagSwatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 7, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), BorderThickness = new Thickness(1) };
        _tagText = new TextBlock { Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center };
        var tagRow = new StackPanel { Orientation = Orientation.Horizontal };
        tagRow.Children.Add(_tagSwatch);
        tagRow.Children.Add(_tagText);
        _tag = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0A, 0x0A, 0x0D)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 10, 5),
            Child = tagRow,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        canvas.Children.Add(_tag);
        root.Children.Add(canvas);

        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x0A, 0x0D)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 8, 16, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 60, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Click a color to copy it.  Esc cancels.",
                Foreground = Brushes.White,
                FontSize = 13.5,
                FontFamily = new FontFamily("Segoe UI"),
            },
        });

        Content = root;

        MouseMove += (_, e) => Follow(e.GetPosition(this));
        MouseLeftButtonDown += (_, e) =>
        {
            Picked = ColorAt(_shot, ToPixel(e.GetPosition(this)));
            Close();
        };
        MouseRightButtonDown += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Follow(Mouse.GetPosition(this));
        };
    }

    (int x, int y) ToPixel(Point p)
    {
        double sx = _shot.PixelWidth / Math.Max(1, ActualWidth), sy = _shot.PixelHeight / Math.Max(1, ActualHeight);
        return (Math.Clamp((int)(p.X * sx), 0, _shot.PixelWidth - 1), Math.Clamp((int)(p.Y * sy), 0, _shot.PixelHeight - 1));
    }

    void Follow(Point p)
    {
        var (x, y) = ToPixel(p);
        _zoom.Source = Neighborhood(_shot, x, y, Around);

        var color = ColorAt(_shot, (x, y));
        _tagText.Text = Hex(color);
        _tagSwatch.Background = new SolidColorBrush(color);

        // the glass sits beside the pointer and flips sides near the screen edges
        double gx = p.X + 22, gy = p.Y + 22;
        if (gx + _glass.Width > ActualWidth) gx = p.X - 22 - _glass.Width;
        if (gy + _glass.Height + 34 > ActualHeight) gy = p.Y - 22 - _glass.Height - 30;
        Canvas.SetLeft(_glass, gx);
        Canvas.SetTop(_glass, gy);
        Canvas.SetLeft(_tag, gx + 8);
        Canvas.SetTop(_tag, gy + _glass.Height + 6);
        _glass.Visibility = _tag.Visibility = Visibility.Visible;
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>The color of one pixel of the picture.</summary>
    public static Color ColorAt(BitmapSource shot, (int x, int y) at)
    {
        var pixel = new byte[4];
        shot.CopyPixels(new Int32Rect(at.x, at.y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]); // the picture is stored blue-green-red
    }

    /// <summary>The square of pixels around a point, padded with black past the picture's edges.</summary>
    static BitmapSource Neighborhood(BitmapSource shot, int x, int y, int around)
    {
        int side = around * 2 + 1;
        var pixels = new byte[side * side * 4];
        int left = Math.Max(0, x - around), top = Math.Max(0, y - around);
        int right = Math.Min(shot.PixelWidth - 1, x + around), bottom = Math.Min(shot.PixelHeight - 1, y + around);
        int w = right - left + 1, h = bottom - top + 1;
        int offset = ((top - (y - around)) * side + (left - (x - around))) * 4;
        shot.CopyPixels(new Int32Rect(left, top, w, h), pixels, side * 4, offset);

        var source = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgr32, null, pixels, side * 4);
        source.Freeze();
        return source;
    }
}
