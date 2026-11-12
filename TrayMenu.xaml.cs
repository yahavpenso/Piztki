using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Piztki.Core;

namespace Piztki;

/// <summary>The menu that opens from the tray icon, drawn in Piztki's own style.</summary>
public partial class TrayMenu : Window
{
    bool _closing;

    public TrayMenu()
    {
        InitializeComponent();
        DataContext = App.Settings;

        StatusText.Text = App.Settings.ShowLevel ? $"Level {App.Settings.Level}" : "A friendly little notch";

        bool hidden = App.Notch?.UserHidden == true;
        HideLabel.Text = hidden ? "Show the notch" : "Hide the notch";
        HideIcon.Text = hidden ? "" : ""; // chevron down to bring it back, up to tuck it away

        SourceInitialized += (_, _) => Native.HideFromAltTab(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            PlaceNearCursor();
            // grows out of the corner nearest the tray icon, then the rows follow
            Fx.Appear(Root, dx: 0, dy: 10, ms: 240, fromScale: 0.9);
            int i = 0;
            foreach (UIElement row in new UIElement[] { RowSettings, RowHide, RowQuit })
                Fx.Appear(row, dx: 0, dy: 8, ms: 260, delay: 60 + i++ * 40);
            Face.Wave();
        };
        Deactivated += (_, _) => CloseSmoothly(); // clicking anywhere else dismisses it
    }

    /// <summary>Sits just above and to the left of the pointer, kept inside the usable screen area.</summary>
    void PlaceNearCursor()
    {
        if (!Native.GetCursorPos(out var p)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double x = p.X / dpi.DpiScaleX, y = p.Y / dpi.DpiScaleY;

        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(x - ActualWidth + 20, area.Left, area.Right - ActualWidth);
        Top = Math.Clamp(y - ActualHeight - 2, area.Top, area.Bottom - ActualHeight);
    }

    void CloseSmoothly(Action? then = null)
    {
        if (_closing) return;
        _closing = true;
        Fx.Disappear(Root, dy: 8, ms: 120, done: () =>
        {
            Close();
            then?.Invoke();
        });
    }

    void Settings_Click(object sender, RoutedEventArgs e)
        => CloseSmoothly(() => ((App)Application.Current).ShowSettings());

    void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (App.Notch is { } notch) notch.UserHidden = !notch.UserHidden;
        CloseSmoothly();
    }

    void Quit_Click(object sender, RoutedEventArgs e)
        => CloseSmoothly(() => Application.Current.Shutdown());
}
