using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Piztki.Core;

namespace Piztki;

// The Discord page's step-by-step guide: five steps, each with a small drawing of where to click
// on Discord's site, and a check mark once Piztki sees the step worked.
public partial class SettingsWindow
{
    sealed record GuideStep(string Title, string Body, string? Action);

    static readonly GuideStep[] Guide =
    {
        new("Create your Discord application",
            "Press the button below. Discord's developer page opens in your browser (sign in if it asks). Press New Application at the top right, type the name Piztki, tick the box, and press Create. It is free.",
            "Open the developer page"),
        new("Copy the Application ID",
            "In your new application, stay on General Information. Find APPLICATION ID, press Copy under it, and paste it into the Application ID box below. A few seconds later this step gets a check mark, and your status starts showing on your profile.",
            null),
        new("Add the redirect (for voice controls)",
            "This step and the next two are only for Mute, Deafen, and who is talking. First press the button below to copy the address. Then on the site: open OAuth2 in the menu on the left, press Add Redirect under Redirects, paste, and press Save Changes at the bottom.",
            "Copy http://localhost"),
        new("Copy the Client Secret",
            "Still on the OAuth2 page, find CLIENT SECRET and press Reset Secret, then Yes. Press Copy and paste it into the Client Secret box below. It is like a password: paste it only here and never send it to anyone.",
            null),
        new("Connect and press Authorize",
            "Press Connect. The Discord app shows a small window asking if Piztki may control your voice settings. Press Authorize. The voice controls then appear in the notch's Sound tab.",
            "Connect"),
    };

    int _guideStep;

    void InitDiscordGuide()
    {
        // open on the first step that still needs doing
        var s = App.Settings;
        var notch = App.Notch;
        _guideStep = s.DiscordAppId.Length == 0 ? 0
            : notch is not { DiscordConnected: true } ? 1
            : s.DiscordSecret.Length == 0 ? 2
            : 4;
        ShowGuideStep(animate: false);
    }

    /// <summary>Whether Piztki can already see that this step worked.</summary>
    bool GuideStepDone(int step)
    {
        var s = App.Settings;
        var notch = App.Notch;
        return step switch
        {
            0 => s.DiscordAppId.Length > 0,
            1 => notch is { DiscordConnected: true },
            2 or 4 => notch is { DiscordVoiceReady: true },
            3 => s.DiscordSecret.Length > 0,
            _ => false,
        };
    }

    void ShowGuideStep(bool animate, int direction = 1)
    {
        var step = Guide[_guideStep];

        GuideCount.Text = $"Step {_guideStep + 1} of {Guide.Length}";
        GuideTitle.Text = step.Title;
        GuideBody.Text = step.Body;

        GuideArt.Child = GuideDrawing(_guideStep);
        GuideAction.Content = step.Action;
        GuideAction.Visibility = step.Action != null ? Visibility.Visible : Visibility.Collapsed;
        GuideBack.Visibility = _guideStep > 0 ? Visibility.Visible : Visibility.Collapsed;
        GuideNext.Visibility = _guideStep < Guide.Length - 1 ? Visibility.Visible : Visibility.Collapsed;

        // the dots along the top: done ones filled, the current one wide
        GuideDots.Children.Clear();
        for (int i = 0; i < Guide.Length; i++)
        {
            var dot = new Border { Height = 6, Width = i == _guideStep ? 22 : 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 5, 0) };
            if (i == _guideStep || GuideStepDone(i)) dot.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else dot.Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
            if (i != _guideStep && GuideStepDone(i)) dot.Opacity = 0.55;
            GuideDots.Children.Add(dot);
        }

        ShowGuideCheck();
        if (animate)
        {
            Fx.Appear(GuideArt, dx: direction * 22, dy: 0, ms: 320);
            Fx.Appear(GuideText, dx: direction * 14, dy: 0, ms: 320, delay: 50);
        }
    }

    /// <summary>The line under the text: a check when the step is done, otherwise what Piztki is waiting for.</summary>
    void ShowGuideCheck()
    {
        if (GuideCheck == null) return;
        bool done = GuideStepDone(_guideStep);
        bool wasDone = (string)GuideCheckIcon.Tag == "done";

        string waiting = _guideStep switch
        {
            0 => "Once the application exists, press Next",
            1 => "Waiting for the Application ID in the box below",
            2 => "Once the redirect is saved, press Next",
            3 => "Waiting for the Client Secret in the box below",
            _ => "Waiting for you to press Authorize in Discord",
        };
        // the first and third steps happen on Discord's site, where Piztki can't look
        string finished = _guideStep switch
        {
            0 => "There is an Application ID. On to the next step",
            1 => "Connected to Discord. Your profile status is on",
            2 or 4 => "Voice controls are connected. All done",
            _ => "The Client Secret is saved",
        };

        GuideCheckText.Text = done ? finished : waiting;
        GuideCheckIcon.Text = done ? "\uE73E" : "\uE916";
        GuideCheckIcon.Tag = done ? "done" : "";
        GuideCheckIcon.SetResourceReference(TextBlock.ForegroundProperty, done ? "AccentBrush" : "SubtleBrush");
        if (done && !wasDone && IsLoaded) Fx.Pop(GuideCheckIcon, from: 0.3, ms: 460);
    }

    void GuideNext_Click(object sender, RoutedEventArgs e)
    {
        if (_guideStep >= Guide.Length - 1) return;
        _guideStep++;
        ShowGuideStep(animate: true, direction: 1);
    }

    void GuideBack_Click(object sender, RoutedEventArgs e)
    {
        if (_guideStep == 0) return;
        _guideStep--;
        ShowGuideStep(animate: true, direction: -1);
    }

    void GuideAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_guideStep)
        {
            case 0:
                DiscordPortal_Click(sender, e);
                break;
            case 2:
                try { Clipboard.SetText(DiscordRpc.RedirectUri); } catch { }
                GuideCheckText.Text = "Copied. Paste it under Redirects";
                break;
            case 4:
                DiscordVoice_Click(sender, e);
                break;
        }
        Fx.Pop(GuideAction, from: 0.9, ms: 300);
    }

    /// <summary>For the selftest: jump straight to a step.</summary>
    internal void SetGuideForTest(int step)
    {
        _guideStep = step;
        ShowGuideStep(animate: false);
    }

    // ---------- the small drawings ----------

    static readonly Brush MockPanel = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x26));
    static readonly Brush MockLine = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    static readonly Brush MockText = new SolidColorBrush(Color.FromRgb(0xB8, 0xB9, 0xC4));

    /// <summary>A simplified picture of the screen for this step, with the thing to press glowing.</summary>
    FrameworkElement GuideDrawing(int step)
    {
        if (step == 4) return AuthorizeDrawing();

        var grid = new Grid { FlowDirection = FlowDirection.LeftToRight }; // Discord's site is always left to right
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        // the site's menu on the left
        var menu = new StackPanel { Margin = new Thickness(12, 12, 8, 0) };
        string? current = step switch { 1 => "General Information", 2 or 3 => "OAuth2", _ => null };
        foreach (var item in step == 0 ? new[] { "Applications", "Teams", "Docs" } : new[] { "General Information", "Installation", "OAuth2", "Bot" })
        {
            bool on = item == current || (step == 0 && item == "Applications");
            menu.Children.Add(new Border
            {
                Background = on ? MockLine : Brushes.Transparent,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 3, 7, 4),
                Margin = new Thickness(0, 0, 0, 2),
                Child = new TextBlock { Text = item, FontSize = 10.5, Foreground = on ? Brushes.White : MockText, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal },
            });
        }
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1E)), CornerRadius = new CornerRadius(10, 0, 0, 10), Child = menu });

        var page = new Grid { Margin = new Thickness(16, 12, 14, 12) };
        Grid.SetColumn(page, 1);
        switch (step)
        {
            case 0:
                page.Children.Add(new TextBlock { Text = "Applications", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
                page.Children.Add(Press("New Application", HorizontalAlignment.Right, VerticalAlignment.Top));
                var cards = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
                for (int i = 0; i < 3; i++)
                    cards.Children.Add(new Border { Width = 58, Height = 44, CornerRadius = new CornerRadius(7), Background = MockLine, Margin = new Thickness(0, 0, 8, 0), Opacity = 0.6 });
                page.Children.Add(cards);
                break;
            case 1:
                page.Children.Add(Field("APPLICATION ID", "1438 2907 5516 0042 91", "Copy"));
                break;
            case 2:
                page.Children.Add(Field("REDIRECTS", "http://localhost", "Add Redirect"));
                page.Children.Add(Press("Save Changes", HorizontalAlignment.Right, VerticalAlignment.Bottom, quiet: true));
                break;
            default:
                page.Children.Add(Field("CLIENT SECRET", "••••••••••••••••••••••", "Reset Secret"));
                break;
        }
        grid.Children.Add(page);
        return grid;
    }

    /// <summary>A label, a value box, and the button to press under it.</summary>
    FrameworkElement Field(string label, string value, string button)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = MockText });
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x18)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5, 9, 6),
            Margin = new Thickness(0, 5, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 190,
            Child = new TextBlock { Text = value, FontSize = 11, Foreground = Brushes.White, FontFamily = new FontFamily("Consolas") },
        });
        stack.Children.Add(Press(button, HorizontalAlignment.Left, VerticalAlignment.Top));
        return stack;
    }

    /// <summary>A button in the drawing. The one to press glows and breathes; "quiet" ones are just shown.</summary>
    FrameworkElement Press(string text, HorizontalAlignment h, VerticalAlignment v, bool quiet = false)
    {
        var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold };
        var button = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11, 5, 11, 6),
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Child = label,
        };
        if (quiet)
        {
            button.Background = MockLine;
            label.Foreground = Brushes.White;
            return button;
        }

        button.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        label.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");

        // a ring that swells and fades around it, again and again: "press here"
        var ring = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Margin = new Thickness(-5), IsHitTestVisible = false };
        ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        var pulse = new DoubleAnimation(0.9, 0, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Fx.EaseOut };
        Timeline.SetDesiredFrameRate(pulse, 30);
        ring.BeginAnimation(OpacityProperty, pulse);

        var host = new Grid { HorizontalAlignment = h, VerticalAlignment = v };
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        host.Children.Add(ring);
        host.Children.Add(button);
        return host;
    }

    /// <summary>The last step happens in the Discord app itself: its "Authorize" window.</summary>
    FrameworkElement AuthorizeDrawing()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        stack.Children.Add(new TextBlock { Text = "An external application", FontSize = 9.5, Foreground = MockText, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = "Piztki", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = "wants to access your Discord account", FontSize = 10.5, Foreground = MockText, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 12) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(Press("Cancel", HorizontalAlignment.Left, VerticalAlignment.Center, quiet: true));
        var go = Press("Authorize", HorizontalAlignment.Left, VerticalAlignment.Center);
        go.Margin = new Thickness(12, 0, 0, 0);
        buttons.Children.Add(go);
        stack.Children.Add(buttons);
        return stack;
    }
}
