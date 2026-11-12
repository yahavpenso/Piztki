using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Piztki.Core;

namespace Piztki;

public partial class SettingsWindow : Window
{
    readonly FrameworkElement[] _pages;
    int _pageIndex;
    bool _closing;

    public SettingsWindow()
    {
        InitializeComponent();
        Icon = App.RenderAppIcon(64); // what the taskbar shows while settings are open
        var s = App.Settings;
        DataContext = s;

        _pages = new FrameworkElement[]
        {
            PageGeneral, PageNotch, PageFeatures, PageMusic, PageMascot, PageStyle, PageFocus, PageWater, PageWeather,
            PageCalendar, PageShelf, PageDiscord, PageAbout,
        };

        // "0.3 s later" reads better than a raw number of milliseconds
        void ShowOffset() => OffsetText.Text = s.LyricsOffsetMs == 0 ? "On time"
            : $"{Math.Abs(s.LyricsOffsetMs) / 1000.0:0.0} s {(s.LyricsOffsetMs > 0 ? "later" : "earlier")}";
        void OnOffsetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppSettings.LyricsOffsetMs)) ShowOffset();
        }
        s.PropertyChanged += OnOffsetChanged;
        Closed += (_, _) => s.PropertyChanged -= OnOffsetChanged;
        ShowOffset();

        Select(Styles, s.NotchAttached ? "Attached" : "Floating");
        BuildScreens();
        CountdownNameBox.Text = s.CountdownName;
        CountdownDateBox.Text = s.CountdownDate;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}";

        Select(Swatches, s.Accent);
        Select(Themes, s.Theme);
        Select(StretchOptions, s.StretchMinutes.ToString());
        Select(Sizes, s.NotchSize);
        Select(Textures, s.Texture);
        Select(Hats, s.Hat);
        _citySearch.Tick += (_, _) => SearchCity();
        _settingCityText = true; // showing the saved city shouldn't start a search
        CityBox.Text = s.WeatherCity;
        _settingCityText = false;

        // the weather page explains what the notch is showing (or why it isn't)
        var notch = App.Notch;
        void ShowWeatherStatus() => WeatherStatus.Text = notch?.WeatherStatus ?? "";
        if (notch != null) notch.WeatherStatusChanged += ShowWeatherStatus;
        Closed += (_, _) => { if (notch != null) notch.WeatherStatusChanged -= ShowWeatherStatus; };
        ShowWeatherStatus();

        void ShowDiscordStatus()
        {
            DiscordStatusText.Text = notch?.DiscordStatus ?? "";
            ShowGuideCheck(); // a step may just have been completed
        }
        if (notch != null) notch.DiscordStatusChanged += ShowDiscordStatus;
        Closed += (_, _) => { if (notch != null) notch.DiscordStatusChanged -= ShowDiscordStatus; };
        InitDiscordGuide();
        ShowDiscordStatus();
        void OnDiscordSetting(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AppSettings.DiscordAppId) or nameof(AppSettings.DiscordSecret)) ShowGuideCheck();
        }
        s.PropertyChanged += OnDiscordSetting;
        Closed += (_, _) => s.PropertyChanged -= OnDiscordSetting;

        Loaded += (_, _) =>
        {
            // the window grows in, then the first page's rows follow one by one
            Fx.Animate(Root, OpacityProperty, 1, 220);
            Fx.Animate(RootScale, ScaleTransform.ScaleXProperty, 1, 380, Fx.Spring);
            Fx.Animate(RootScale, ScaleTransform.ScaleYProperty, 1, 380, Fx.Spring);
            Stagger(_pages[0], startDelay: 120);
        };
    }

    static void Select(Panel options, string value)
    {
        foreach (RadioButton option in options.Children)
            option.IsChecked = string.Equals((string)option.Tag, value, StringComparison.OrdinalIgnoreCase);
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages == null || Nav.SelectedIndex < 0 || Nav.SelectedIndex == _pageIndex) return;

        var previous = _pages[_pageIndex];
        int direction = Nav.SelectedIndex > _pageIndex ? 1 : -1;
        _pageIndex = Nav.SelectedIndex;
        var next = _pages[_pageIndex];
        PageScroll.ScrollToTop(); // every page starts from its title

        Fx.Disappear(previous, dy: -direction * 10, ms: 110, done: () =>
        {
            if (!ReferenceEquals(_pages[_pageIndex], previous)) previous.Visibility = Visibility.Collapsed;
        });

        Fx.Appear(next, dx: 0, dy: 0, ms: 1, delay: 100);
        Stagger(next, startDelay: 110, dy: direction * 16);
    }

    /// <summary>Each row of a page slides in a beat after the one above it.</summary>
    static void Stagger(FrameworkElement page, int startDelay, double dy = 14)
    {
        if (page is not Panel panel) return;
        int i = 0;
        foreach (UIElement child in panel.Children)
            Fx.Appear(child, dx: 0, dy: dy, ms: 340, delay: startDelay + i++ * 45);
    }

    void Swatch_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Accent = (string)((RadioButton)sender).Tag;
        PreviewMascot.Celebrate();
    }

    void Size_Click(object sender, RoutedEventArgs e)
        => App.Settings.NotchSize = (string)((RadioButton)sender).Tag;

    void NotchStyle_Click(object sender, RoutedEventArgs e)
        => App.Settings.NotchAttached = (string)((RadioButton)sender).Tag == "Attached";

    /// <summary>One button per connected monitor; the one holding the notch is highlighted.</summary>
    void BuildScreens()
    {
        var s = App.Settings;
        var all = System.Windows.Forms.Screen.AllScreens;
        var current = all.FirstOrDefault(x => x.DeviceName == s.ScreenName) ?? System.Windows.Forms.Screen.PrimaryScreen;

        Screens.Children.Clear();
        for (int i = 0; i < all.Length; i++)
        {
            var screen = all[i];
            var label = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            label.Children.Add(new TextBlock { Text = $"Screen {i + 1}", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center });
            label.Children.Add(new TextBlock
            {
                Text = $"{screen.Bounds.Width}×{screen.Bounds.Height}" + (screen.Primary ? " · Main" : ""),
                FontSize = 11,
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var button = new RadioButton
            {
                Style = (Style)FindResource("Segment"),
                Width = 150,
                Height = 50,
                Margin = new Thickness(0, 0, 8, 0),
                Content = label,
                GroupName = "NotchScreen",
                IsChecked = ReferenceEquals(screen, current) || screen.DeviceName == current?.DeviceName,
            };
            // the main screen is stored as "" so the notch follows it if the main screen ever changes
            button.Click += (_, _) => s.ScreenName = screen.Primary ? "" : screen.DeviceName;
            Screens.Children.Add(button);
        }

        if (all.Length < 2) ScreenHint.Text = "Only one screen is connected right now";
    }

    void City_Step(object sender, RoutedEventArgs e)
    {
        // Tag is "which clock, direction", e.g. "2,-1"
        var parts = ((string)((FrameworkElement)sender).Tag).Split(',');
        int step = int.Parse(parts[1]);
        var s = App.Settings;
        if (parts[0] == "1") s.WorldCity1 = Almanac.NextCity(s.WorldCity1, step);
        else s.WorldCity2 = Almanac.NextCity(s.WorldCity2, step);
    }

    void Countdown_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Keyboard.ClearFocus(); // leaving the box saves it
    }

    void Countdown_Changed(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.CountdownName = CountdownNameBox.Text.Trim();

        string date = CountdownDateBox.Text.Trim();
        bool ok = date.Length == 0 || Almanac.TryParseDate(date, out _);
        if (ok) s.CountdownDate = date;

        CountdownHint.Text = ok
            ? "Year-month-day, like 2027-01-31. Leave empty for no countdown"
            : "That isn't a date Piztki can read. Write it like 2027-01-31";
        CountdownHint.SetResourceReference(TextBlock.ForegroundProperty, ok ? "SubtleBrush" : "AccentBrush");
    }

    /// <summary>A theme also brings the glow color that suits it; the swatches can still change it after.</summary>
    void Theme_Click(object sender, RoutedEventArgs e)
    {
        string theme = (string)((RadioButton)sender).Tag;
        App.Settings.Theme = theme;
        App.Settings.Accent = NotchThemes.Look(theme).accent;
        Select(Swatches, App.Settings.Accent);
        Fx.Pop(PreviewMascot, from: 0.88, ms: 380);
    }

    void DiscordPortal_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true }); }
        catch { }
    }

    async void DiscordVoice_Click(object sender, RoutedEventArgs e)
    {
        if (App.Notch == null) return;
        DiscordVoiceBtn.IsEnabled = false;
        DiscordVoiceBtn.Content = "Waiting…";
        try { await App.Notch.ConnectDiscordVoice(); }
        catch (Exception ex) { App.LogError("Discord connect button", ex); }
        finally
        {
            DiscordVoiceBtn.IsEnabled = true;
            DiscordVoiceBtn.Content = "Connect";
        }
    }

    void Stretch_Click(object sender, RoutedEventArgs e)
        => App.Settings.StretchMinutes = int.Parse((string)((RadioButton)sender).Tag);

    void Texture_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Texture = (string)((RadioButton)sender).Tag;
        Fx.Pop(PreviewMascot, from: 0.88, ms: 380);
    }

    void Hat_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Hat = (string)((RadioButton)sender).Tag;
        PreviewMascot.Celebrate();
    }

    // ----- weather city: type, see matching places, click one -----

    readonly System.Windows.Threading.DispatcherTimer _citySearch = new() { Interval = TimeSpan.FromMilliseconds(350) };
    List<WeatherService.Place> _cityMatches = new();
    int _citySearchId;
    bool _settingCityText;

    void City_TextChanged(object sender, TextChangedEventArgs e)
    {
        CityHint.Visibility = CityBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_settingCityText) return;

        // wait for a short pause in typing before asking, instead of searching on every letter
        _citySearch.Stop();
        _citySearch.Start();
    }

    async void SearchCity()
    {
        _citySearch.Stop();
        int id = ++_citySearchId;
        string query = CityBox.Text.Trim();

        if (query.Length < 2)
        {
            _cityMatches = new();
            CityResults.Children.Clear();
            return;
        }

        var places = await WeatherService.SearchAsync(query);
        if (id != _citySearchId) return; // the user kept typing; a newer search is on its way

        _cityMatches = places;
        CityResults.Children.Clear();

        if (places.Count == 0)
        {
            CityResults.Children.Add(new TextBlock
            {
                Text = "No place found with that name. Check the spelling, or your internet connection.",
                FontSize = 12,
                Foreground = (Brush)FindResource("SubtleBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 0, 0),
            });
            return;
        }

        int i = 0;
        foreach (var place in places)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = place.Name, FontSize = 13 });
            if (place.Detail.Length > 0)
                text.Children.Add(new TextBlock { Text = place.Detail, FontSize = 11.5, Foreground = (Brush)FindResource("SubtleBrush") });

            var row = new Button { Style = (Style)FindResource("PlaceRow"), Content = text };
            row.Click += (_, _) => PickCity(place);
            CityResults.Children.Add(row);
            Fx.Appear(row, dx: 0, dy: 8, ms: 240, delay: i++ * 35);
        }
    }

    void PickCity(WeatherService.Place place)
    {
        var s = App.Settings;

        _settingCityText = true;
        CityBox.Text = place.Display;
        _settingCityText = false;
        _cityMatches = new();
        CityResults.Children.Clear();
        Keyboard.ClearFocus();

        // the coordinates go in first: changing the city name is what triggers the weather lookup
        s.WeatherLat = place.Latitude;
        s.WeatherLon = place.Longitude;
        s.WeatherPlace = place.Display;
        s.WeatherCity = place.Display;
        if (!s.ShowWeather) s.ShowWeather = true; // picking a city obviously means "show it"
    }

    async void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (!LocateButton.IsEnabled) return;
        LocateButton.IsEnabled = false; // one lookup at a time
        LocateStatus.Text = "Looking…";

        var place = await WeatherService.LocateAsync();

        LocateButton.IsEnabled = true;
        if (place == null)
        {
            LocateStatus.Text = "Couldn't work it out. Type your city above instead.";
            return;
        }

        LocateStatus.Text = "Found " + place.Name;
        Fx.Appear(LocateStatus, dx: 8, dy: 0, ms: 280);
        PickCity(place);
    }

    void City_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter takes the top suggestion
        if (e.Key == Key.Enter && _cityMatches.Count > 0) PickCity(_cityMatches[0]);
    }

    void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PreviewMascot.Jump();
        e.Handled = true; // poking the mascot shouldn't start dragging the window
    }

    void Drag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBox) Keyboard.ClearFocus();
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        Fx.Animate(RootScale, ScaleTransform.ScaleXProperty, 0.95, 150, Fx.EaseIn);
        Fx.Animate(RootScale, ScaleTransform.ScaleYProperty, 0.95, 150, Fx.EaseIn);
        Fx.Animate(Root, OpacityProperty, 0, 150, Fx.EaseIn, done: Close);
    }

    void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
