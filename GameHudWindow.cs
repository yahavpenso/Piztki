using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Piztki;

/// <summary>
/// Game mode: a tiny strip at the top middle of the screen with the time and how hard the PC is
/// working, shown while a game fills the screen. It never takes the keyboard or the mouse: clicks
/// go straight through it to the game.
/// </summary>
public sealed class GameHudWindow : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    readonly TextBlock _text;

    public GameHudWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _text = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF3, 0xFA)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x0A, 0x0A, 0x0D)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 4, 10, 5),
            Child = _text,
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
    }

    public string Line
    {
        get => _text.Text;
        set => _text.Text = value;
    }

    readonly TranslateTransform _slide = new();

    /// <summary>Centres the strip along the top edge of a screen (given in real pixels), a little larger.</summary>
    public void PlaceTopCentre(System.Drawing.Rectangle screen, double down = 14)
    {
        _text.FontSize = 14;
        Centre(screen, down);
    }

    System.Drawing.Rectangle _screen;
    double _down;
    bool _centred;

    /// <summary>Keeps the strip in the middle of the top edge, also when its text gets longer or shorter.</summary>
    void Centre(System.Drawing.Rectangle screen, double down)
    {
        _screen = screen;
        _down = down;
        if (!_centred)
        {
            _centred = true;
            SizeChanged += (_, _) => Recentre();
        }
        UpdateLayout();
        Recentre();
    }

    void Recentre()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = _screen.Left / scale + (_screen.Width / scale - ActualWidth) / 2;
        Top = _screen.Top / scale + _down;
    }

    /// <summary>Drops in from above while fading in.</summary>
    public void SlideIn()
    {
        ((FrameworkElement)Content).RenderTransform = _slide;
        _slide.Y = -24;
        Piztki.Core.Fx.Animate(_slide, TranslateTransform.YProperty, 0, 520, Piztki.Core.Fx.Spring);
        Piztki.Core.Fx.Animate(this, OpacityProperty, 1, 260, Piztki.Core.Fx.EaseOut);
    }

    public void FadeAway(Action done)
    {
        Piztki.Core.Fx.Animate(_slide, TranslateTransform.YProperty, -18, 320, Piztki.Core.Fx.EaseIn);
        Piztki.Core.Fx.Animate(this, OpacityProperty, 0, 320, Piztki.Core.Fx.EaseIn, done: done);
    }

    /// <summary>Puts the strip in the middle of the top edge of a screen (given in real pixels), where the notch sits.</summary>
    public void PlaceOn(System.Drawing.Rectangle screen) => Centre(screen, 6);
}
