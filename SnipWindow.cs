using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Piztki;

/// <summary>
/// The screenshot picker: shows a frozen picture of the whole desktop, dimmed, and lets the user
/// drag a rectangle over it. The chosen area (in the picture's own pixels) ends up in Selection.
/// </summary>
public sealed class SnipWindow : Window
{
    readonly BitmapSource _shot;
    readonly RectangleGeometry _hole = new();
    readonly Rectangle _frame;
    readonly Border _sizeTag;
    readonly TextBlock _sizeText;
    Point? _start;

    public Int32Rect? Selection { get; private set; }

    public SnipWindow(BitmapSource shot)
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

        // a dark sheet over everything, with a hole where the selection is
        var sheet = new GeometryGroup { FillRule = FillRule.EvenOdd };
        sheet.Children.Add(new RectangleGeometry(new Rect(0, 0, Width, Height)));
        sheet.Children.Add(_hole);
        root.Children.Add(new System.Windows.Shapes.Path { Data = sheet, Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) });

        var canvas = new Canvas();
        _frame = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1.5, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
        canvas.Children.Add(_frame);

        _sizeText = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI") };
        _sizeTag = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x0A, 0x0D)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 3, 8, 4),
            Child = _sizeText,
            Visibility = Visibility.Collapsed,
        };
        canvas.Children.Add(_sizeTag);
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
                Text = "Drag over the part you want.  Esc cancels.",
                Foreground = Brushes.White,
                FontSize = 13.5,
                FontFamily = new FontFamily("Segoe UI"),
            },
        });

        Content = root;

        MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(this);
            CaptureMouse();
            Draw(new Rect(_start.Value, _start.Value));
        };
        MouseMove += (_, e) =>
        {
            if (_start is { } start) Draw(new Rect(start, e.GetPosition(this)));
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } start) return;
            ReleaseMouseCapture();
            Finish(new Rect(start, e.GetPosition(this)));
        };
        MouseRightButtonDown += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => { Activate(); Focus(); };
    }

    void Draw(Rect area)
    {
        _hole.Rect = area;
        _frame.Visibility = Visibility.Visible;
        Canvas.SetLeft(_frame, area.X);
        Canvas.SetTop(_frame, area.Y);
        _frame.Width = area.Width;
        _frame.Height = area.Height;

        var pixels = ToPixels(area);
        _sizeText.Text = $"{pixels.Width} × {pixels.Height}";
        _sizeTag.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sizeTag, area.X);
        Canvas.SetTop(_sizeTag, area.Bottom + 6 > ActualHeight - 30 ? area.Y - 28 : area.Bottom + 6);
    }

    void Finish(Rect area)
    {
        var pixels = ToPixels(area);
        if (pixels.Width >= 6 && pixels.Height >= 6) Selection = pixels; // a stray click selects nothing
        Close();
    }

    /// <summary>Window coordinates to pixels of the captured picture (they differ on scaled displays).</summary>
    Int32Rect ToPixels(Rect area)
    {
        double sx = _shot.PixelWidth / Math.Max(1, ActualWidth), sy = _shot.PixelHeight / Math.Max(1, ActualHeight);
        int x = Math.Clamp((int)Math.Round(area.X * sx), 0, _shot.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(area.Y * sy), 0, _shot.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Round(area.Width * sx), 0, _shot.PixelWidth - x);
        int h = Math.Clamp((int)Math.Round(area.Height * sy), 0, _shot.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    // ---------- capturing and saving ----------

    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);

    /// <summary>A picture of every screen, exactly as it looks right now.</summary>
    public static BitmapSource CaptureDesktop()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            // BitBlt with CAPTUREBLT includes see-through windows, which Graphics.CopyFromScreen leaves out
            IntPtr screen = GetDC(IntPtr.Zero), target = g.GetHdc();
            try
            {
                BitBlt(target, 0, 0, bounds.Width, bounds.Height, screen, bounds.Left, bounds.Top, SRCCOPY | CAPTUREBLT);
            }
            finally
            {
                g.ReleaseHdc(target);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * bitmap.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Cuts the area out of the picture and saves it as a PNG in Pictures\Piztki. Returns the file.</summary>
    public static (string path, BitmapSource image) SaveCrop(BitmapSource shot, Int32Rect area, string? folder = null)
    {
        var crop = new CroppedBitmap(shot, area);
        crop.Freeze();

        folder ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Piztki");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop));
        using (var file = File.Create(path)) encoder.Save(file);
        return (path, crop);
    }
}
