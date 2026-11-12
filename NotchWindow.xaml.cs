using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Piztki.Controls;
using Piztki.Core;

namespace Piztki;

// The notch itself: the pill, expanding, tabs, the every-second tick, music, to-do, and water.
// Timers, shelf, clipboard, system stats, weather, levels, and popups live in NotchWindow.Features.cs.
public partial class NotchWindow : Window
{
    const double MiniHeight = 38;
    const double FullWidth = 522;
    const double FullHeight = 250;
    const double HiddenY = -70;
    const double MinMiniWidth = 128;
    const double MaxMiniWidth = 640;

    static readonly Geometry DropShape = Geometry.Parse("M 5,0 C 5,0 10,6 10,9.5 A 5,5 0 1 1 0,9.5 C 0,6 5,0 5,0 Z");
    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    readonly AppSettings _s = App.Settings;
    readonly MediaService _media = new();
    readonly LyricsService _lyrics = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _look = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
    readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _collapse = new() { Interval = TimeSpan.FromMilliseconds(380) };
    readonly DispatcherTimer _beat = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly BeatService _beatService = new();

    RadioButton[] _tabs = Array.Empty<RadioButton>();
    FrameworkElement[] _panels = Array.Empty<FrameworkElement>();
    FrameworkElement? _currentPanel;
    int _tabIndex;

    IntPtr _hwnd;
    bool _ready;
    bool _expanded;
    bool _hiddenForFullscreen;
    double _miniWidth = 150;
    string _miniKind = "";

    string _peekText = "";
    DateTime _peekUntil = DateTime.MinValue;
    DateTime _happyUntil = DateTime.MinValue;

    DateTime _nextWater;
    int _lastWaterCount = -1;
    bool _barsRunning;
    string _lastSong = "";
    string _lastLyric = "";
    bool _seeking;
    Color? _songColor;
    DispatcherTimer? _accentTimer;
    Color _accentFrom, _accentTo, _accentNow;
    DateTime _accentStart;
    bool _accentOverride, _accentRelease;
    TodoItem? _justAdded;
    Native.POINT _lastCursor;
    bool _lastCursorExpanded;
    int _lookCount;

    public NotchWindow()
    {
        InitializeComponent();
        DataContext = _s;

        _tabs = new[] { TabHome, TabMusic, TabFocus, TabTodo, TabNotes, TabWater, TabShelf, TabShortcuts, TabClip, TabSound, TabSystem, TabCal };
        _panels = new FrameworkElement[] { PanelHome, PanelMusic, PanelFocus, PanelTodo, PanelNotes, PanelWater, PanelShelf, PanelShortcuts, PanelClip, PanelSound, PanelSystem, PanelCal };

        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;

        TodoList.ItemsSource = _s.Todos;
        TodoInput.TextChanged += (_, _) =>
            TodoHint.Visibility = TodoInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _tick.Tick += (_, _) => Tick();
        _look.Tick += (_, _) =>
        {
            FollowCursor();
            if (++_lookCount % 2 == 0)
            {
                PollVolume(); // about six times a second is plenty for the popups
                PollKeys();
            }
        };
        _fast.Tick += (_, _) => FastTick();
        _beat.Tick += (_, _) => BeatTick();
        _collapse.Tick += (_, _) =>
        {
            _collapse.Stop();
            // a file being dragged over the notch doesn't count as "mouse over", so check that too
            if (DateTime.Now - _dragOverAt < TimeSpan.FromMilliseconds(600))
            {
                _collapse.Start();
                return;
            }
            if (!Island.IsMouseOver && !TypingInNotch && !_seeking && !_quickDrag) SetExpanded(false);
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.HideFromAltTab(_hwnd);
            StartClipboardWatch();
        };

        Loaded += async (_, _) =>
        {
            RollWaterDay();
            _pomoLeft = TimeSpan.FromMinutes(_s.FocusMinutes);
            _nextWater = DateTime.Now.AddMinutes(_s.WaterIntervalMinutes);
            IslandScale.ScaleX = IslandScale.ScaleY = _s.NotchScale;

            MoveToChosenScreen(animate: false);
            if (_s.ShelfClearOnStart && !_selfTest) _s.Shelf.Clear();
            LoadSavedClips();
            InitCalendar();
            InitTools();
            InitExtras();
            InitFortnite();
            InitDiscord();
            WatchNetwork();
            _lastCaps = Native.CapsLockOn;
            _lastLanguage = Native.KeyboardLanguage;

            _ready = true;
            ApplySettings();
            ApplyFeatures();
            UpdateTimerUi(animate: false);
            UpdateWater();
            UpdateMusic();
            UpdateClock();
            UpdateLevel(animate: false);
            RebuildShelf();
            RebuildClip();
            UpdateMood();
            UpdateMini(animate: false);

            // drop in from above the screen
            Fx.Animate(IslandT, TranslateTransform.YProperty, 0, 620, Fx.Spring, delay: 250);

            _tick.Start();
            _look.Start();

            _lyrics.Changed += OnLyricsChanged;
            if (_s.MusicOpensOnLyrics) OpenLyrics(true, animate: false);
            _weather.Changed += UpdateWeather;
            _ = _weather.RefreshAsync(_s, force: true);

            _media.Changed += UpdateMusic;
            await _media.InitAsync();
        };

        Deactivated += (_, _) =>
        {
            if (!Island.IsMouseOver && !_selfTest && !_quickDrag) SetExpanded(false);
        };

        _s.PropertyChanged += OnSettingChanged;
    }

    // ---------- settings ----------

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_ready) return;

        switch (e.PropertyName)
        {
            case nameof(AppSettings.FocusMinutes) when _pomo == PomoState.Idle:
                _pomoLeft = TimeSpan.FromMinutes(_s.FocusMinutes);
                UpdateTimerUi(animate: false);
                break;
            case nameof(AppSettings.WaterIntervalMinutes):
            case nameof(AppSettings.WaterEnabled):
                _nextWater = DateTime.Now.AddMinutes(_s.WaterIntervalMinutes);
                break;
            case nameof(AppSettings.WaterCount):
            case nameof(AppSettings.WaterGoal):
                UpdateWater();
                break;
            case nameof(AppSettings.NotchSize):
                Fx.Animate(IslandScale, ScaleTransform.ScaleXProperty, _s.NotchScale, 380, Fx.Spring);
                Fx.Animate(IslandScale, ScaleTransform.ScaleYProperty, _s.NotchScale, 380, Fx.Spring);
                break;
            case nameof(AppSettings.ShowMusic):
            case nameof(AppSettings.ShowFocus):
            case nameof(AppSettings.ShowTodo):
            case nameof(AppSettings.ShowWater):
            case nameof(AppSettings.ShowShelf):
            case nameof(AppSettings.ShowClipboard):
            case nameof(AppSettings.ShowSystem):
            case nameof(AppSettings.ShowCalendar):
            case nameof(AppSettings.ShowNotes):
            case nameof(AppSettings.ShowSound):
            case nameof(AppSettings.ShowShortcuts):
                ApplyFeatures();
                break;
            case nameof(AppSettings.ShowDownloads):
                ApplyDownloadWatch();
                RebuildHome();
                break;
            case nameof(AppSettings.PrivacyDots):
                PollPrivacy(announce: false);
                break;
            case nameof(AppSettings.DiscordAppId):
                ResetDiscord();
                return;
            case nameof(AppSettings.DiscordPresence):
                _presenceKey = "?"; // forces the profile to be brought up to date (or cleared) on the next tick
                return;
            case nameof(AppSettings.DiscordVoice):
                UpdateDiscordRow();
                DiscordStatusChanged?.Invoke();
                return;
            case nameof(AppSettings.DiscordSecret):
            case nameof(AppSettings.DiscordAccess):
            case nameof(AppSettings.DiscordRefresh):
                return; // nothing on screen depends on these
            case nameof(AppSettings.HudNetwork):
                SetOffline(_offline);
                break;
            case nameof(AppSettings.ShowSearch):
                ApplyFeatures(); // the search box alone is enough for Home to exist
                break;
            case nameof(AppSettings.ShowNotifications):
                _notifications.Reset();
                break;
            case nameof(AppSettings.StretchReminders):
            case nameof(AppSettings.StretchMinutes):
                _activeSince = DateTime.Now;
                break;
            case nameof(AppSettings.GameMode):
                UpdateHidden();
                break;
            case nameof(AppSettings.Cs2Summary):
                ApplyCs2(announce: true);
                break;
            case nameof(AppSettings.MatchSummary):
                ApplyCs2(announce: false);
                ApplyFeatures();
                break;
            case nameof(AppSettings.LastMatchHead):
                ApplyFeatures(); // the card can be the only thing on Home, so the Home tab may come or go with it
                break;
            case nameof(AppSettings.LastMatchFile):
            case nameof(AppSettings.LastMatchDetail):
            case nameof(AppSettings.LastMatchGame):
            case nameof(AppSettings.LastMatchWhen):
            case nameof(AppSettings.Cs2Token):
            case nameof(AppSettings.LastMatchIcon):
            case nameof(AppSettings.LastMatchMore):
                return; // the card is redrawn by LastMatchHead, which is always set last
            case nameof(AppSettings.NoteIndex):
                return; // only remembers which note was open; nothing on screen depends on it
            case nameof(AppSettings.ScreenName):
                MoveToChosenScreen(animate: true);
                break;
            case nameof(AppSettings.WeekStartsMonday):
            case nameof(AppSettings.WorldCity1):
            case nameof(AppSettings.WorldCity2):
            case nameof(AppSettings.CountdownName):
            case nameof(AppSettings.CountdownDate):
                RebuildCalendar();
                UpdateCalendarSide();
                break;
            case nameof(AppSettings.ClipMax):
            case nameof(AppSettings.ClipRemember):
                TrimClips();
                break;
            case nameof(AppSettings.Use24Hour):
                UpdateClock();
                break;
            case nameof(AppSettings.ShowLyrics):
                _lastSong = ""; // makes UpdateMusic load (or drop) the lyrics for the current song
                break;
            case nameof(AppSettings.ShowWeather):
            case nameof(AppSettings.WeatherCity):
            case nameof(AppSettings.Fahrenheit):
                _ = _weather.RefreshAsync(_s, force: true);
                break;
            case nameof(AppSettings.Xp):
            case nameof(AppSettings.ShowLevel):
                UpdateLevel(animate: true);
                break;
            case nameof(AppSettings.MascotMoods):
                UpdateMood();
                break;
        }

        ApplySettings();
        UpdateMusic();
    }

    void ApplySettings()
    {
        var mascot = _s.ShowMascot ? Visibility.Visible : Visibility.Collapsed;
        MiniMascot.Visibility = mascot;
        MascotHost.Visibility = mascot;
        LevelRow.Visibility = _s.ShowMascot && _s.ShowLevel ? Visibility.Visible : Visibility.Collapsed;
        Mini.Margin = _s.ShowMascot ? new Thickness(5, 0, 14, 0) : new Thickness(14, 0, 14, 0);

        Fx.Speed = _s.FastAnimations ? 0.55 : 1;
        ApplyTheme();

        // attached: flat against the top edge with square top corners; floating: a pill just below it
        Shell.Margin = new Thickness(0, _s.NotchAttached ? 0 : 6, 0, 0);
        var corners = _s.NotchAttached ? new CornerRadius(0, 0, 22, 22) : new CornerRadius(24);
        var inner = _s.NotchAttached ? new CornerRadius(0, 0, 22, 22) : new CornerRadius(23);
        Island.CornerRadius = corners;
        // attached: no outline (a line down the sides would cut the shoulders off from the body)
        Island.BorderThickness = new Thickness(_s.NotchAttached ? 0 : _s.Theme == "Neon" ? 1.5 : 1);
        var shoulders = _s.NotchAttached ? Visibility.Visible : Visibility.Collapsed;
        ShoulderLeft.Visibility = shoulders;
        ShoulderRight.Visibility = shoulders;
        Tint.CornerRadius = inner;
        Glow.CornerRadius = inner;

        if (!_s.EyesFollowCursor)
        {
            MiniMascot.LookAt(0, 0);
            BigMascot.LookAt(0, 0);
        }
    }

    /// <summary>Shows only the tabs the user kept switched on, and keeps a valid one selected.</summary>
    void ApplyFeatures()
    {
        bool anyFeature = _s.ShowMusic || _s.ShowFocus || _s.ShowTodo || _s.ShowWater || _s.ShowShelf || _s.ShowClipboard || _s.ShowSystem
                          || _s.ShowNotes || _s.ShowShortcuts || _s.ShowSound
                          // these have no tab of their own but do live on Home: the next event, the search box, the last game
                          || _s.ShowCalendar || _s.ShowSearch || (_s.MatchSummary && _s.LastMatchHead.Length > 0);
        // the first entry is the home tab, which exists as long as there is anything to show on it
        bool[] on = { anyFeature, _s.ShowMusic, _s.ShowFocus, _s.ShowTodo, _s.ShowNotes, _s.ShowWater, _s.ShowShelf, _s.ShowShortcuts,
                      _s.ShowClipboard, _s.ShowSound, _s.ShowSystem, _s.ShowCalendar };
        RebuildHome();
        for (int i = 0; i < _tabs.Length; i++)
            _tabs[i].Visibility = on[i] ? Visibility.Visible : Visibility.Collapsed;

        bool any = Array.IndexOf(on, true) >= 0;
        TabStrip.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        if (!any)
        {
            if (_currentPanel != null)
            {
                _currentPanel.Visibility = Visibility.Collapsed;
                _currentPanel = null;
            }
            foreach (var tab in _tabs) tab.IsChecked = false;
            if (NoFeatures.Visibility != Visibility.Visible) Fx.Appear(NoFeatures, dy: 8);
            return;
        }

        NoFeatures.Visibility = Visibility.Collapsed;

        int current = Array.FindIndex(_tabs, t => t.IsChecked == true);
        if (current < 0 || !on[current])
            _tabs[Array.IndexOf(on, true)].IsChecked = true; // raises Tab_Checked
        else if (_currentPanel == null)
            SelectTab(current, animate: false);

        // tab positions only settle after layout
        Dispatcher.BeginInvoke(() => MoveIndicator(animate: true), DispatcherPriority.Loaded);
    }

    // ---------- expand / collapse ----------

    void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_selfTest) return; // the QA walkthrough opens and closes the notch itself
        _collapse.Stop();
        if (!_s.ClickToOpen) SetExpanded(true);
    }

    void Island_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_s.ClickToOpen && !_expanded) SetExpanded(true);
        QuickDragDown(e);
    }

    void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_selfTest) return;
        _collapse.Stop();
        _collapse.Start();
    }

    void SetExpanded(bool expanded)
    {
        if (_expanded == expanded || _hiddenForFullscreen) return;
        _expanded = expanded;

        // Only the side that is showing stays in the visual tree. A hidden-but-present panel would
        // keep its mascot animating (and WPF redrawing) for nothing.
        if (expanded)
        {
            Full.Visibility = Visibility.Visible;
            Full.UpdateLayout(); // tab positions are needed right away for the indicator
            // the calendar always reopens on your events; the world clocks are a side trip
            if (CalWorld.Visibility == Visibility.Visible) ShowCalendarSide(world: false, animate: false);
            UpdateClock();
            MoveIndicator(animate: false);
            Fx.Appear(LeftCol, dx: -16, dy: 0, ms: 360, delay: 70, fromScale: 0.82);
            Fx.Appear(RightCol, dx: 18, dy: 0, ms: 360, delay: 120);
            if (_s.WaveOnOpen) BigMascot.Wave();
            RefreshVisiblePanel();
        }
        else if (TypingInNotch)
        {
            Keyboard.ClearFocus();
        }

        Full.IsHitTestVisible = expanded;
        if (expanded)
        {
            Fx.Animate(Full, OpacityProperty, 1, 160, delay: 40);
            Fx.Animate(Mini, OpacityProperty, 0, 90, done: () => { if (_expanded) Mini.Visibility = Visibility.Collapsed; });
        }
        else
        {
            Mini.Visibility = Visibility.Visible;
            Fx.Animate(Full, OpacityProperty, 0, 110, done: () => { if (!_expanded) Full.Visibility = Visibility.Collapsed; });
            Fx.Animate(Mini, OpacityProperty, 1, 240, delay: 130);
            Fx.Pop(MiniMascot, from: 0.7, ms: 420, delay: 130);
        }
        AnimateIsland();
        UpdateFastTimer();
    }

    void AnimateIsland(bool animate = true)
    {
        double w = _expanded ? FullWidth : _miniWidth;
        double h = _expanded ? FullHeight : MiniHeight;

        if (!animate)
        {
            Island.BeginAnimation(WidthProperty, null);
            Island.BeginAnimation(HeightProperty, null);
            Island.Width = w;
            Island.Height = h;
            return;
        }

        // Opening and closing get a springy overshoot. When only the pill's width changes (a new
        // lyric line every few seconds) it glides instead, so it doesn't bounce all song long.
        bool resizeOnly = !_expanded && Math.Abs(Island.ActualHeight - MiniHeight) < 1;
        IEasingFunction ease = resizeOnly
            ? Fx.EaseOut
            : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = _expanded ? 0.4 : 0.3 };
        var time = TimeSpan.FromMilliseconds((_expanded ? 420 : resizeOnly ? 300 : 340) * Fx.Speed);
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(w, time) { EasingFunction = ease });
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(h, time) { EasingFunction = ease });
    }

    // ---------- tabs ----------

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        int index = Array.IndexOf(_tabs, (RadioButton)sender);
        if (index >= 0) SelectTab(index, animate: true);
    }

    void SelectTab(int index, bool animate)
    {
        var next = _panels[index];
        if (ReferenceEquals(next, _currentPanel))
        {
            MoveIndicator(animate);
            return;
        }

        var previous = _currentPanel;
        int direction = index > _tabIndex ? 1 : -1;
        _tabIndex = index;
        _currentPanel = next;

        if (previous != null)
        {
            if (animate)
                Fx.Disappear(previous, dx: -direction * 18, ms: 130, done: () =>
                {
                    if (!ReferenceEquals(_currentPanel, previous)) previous.Visibility = Visibility.Collapsed;
                });
            else
                previous.Visibility = Visibility.Collapsed;
        }

        if (animate) Fx.Appear(next, dx: direction * 24, dy: 0, ms: 320, delay: 80);
        else Fx.Appear(next, dx: 0, dy: 0, ms: 1);

        MoveIndicator(animate);
        RefreshVisiblePanel();
        UpdateFastTimer();
    }

    void MoveIndicator(bool animate)
    {
        if (_currentPanel == null || !TabStrip.IsVisible) return;
        var tab = _tabs[_tabIndex];
        if (!tab.IsVisible) return;

        double x = tab.TranslatePoint(new Point(0, 0), TabStrip).X;
        if (animate)
        {
            Fx.Animate(TabIndicatorT, TranslateTransform.XProperty, x, 340, Fx.Spring);
        }
        else
        {
            TabIndicatorT.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicatorT.X = x;
        }
    }

    /// <summary>Brings the tab that just came into view up to date straight away.</summary>
    void RefreshVisiblePanel()
    {
        if (!_expanded) return;
        if (ReferenceEquals(_currentPanel, PanelHome)) UpdateHome();
        if (ReferenceEquals(_currentPanel, PanelCal))
        {
            RebuildCalendar();
            UpdateCalendarSide();
        }
        if (ReferenceEquals(_currentPanel, PanelSystem)) UpdateSystem(animate: false);
        if (ReferenceEquals(_currentPanel, PanelMusic)) UpdateSeek();
        if (ReferenceEquals(_currentPanel, PanelSound)) UpdateSound(rebuild: true);
        if (ReferenceEquals(_currentPanel, PanelFocus) && StatsBody.Visibility == Visibility.Visible) RebuildStats(animate: true);
    }

    void Settings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowSettings();

    void Mascot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BigMascot.Jump();
        _happyUntil = DateTime.Now.AddSeconds(4);
        UpdateMood();
    }

    // ---------- every second ----------

    void Tick()
    {
        var now = DateTime.Now;

        RollWaterDay();
        TickTimers();
        TickEvents();
        TickBattery();
        TickTools();
        TickExtras();
        TickDiscord();
        UpdateMood();

        if (_s.WaterEnabled && _s.ShowWater && now >= _nextWater)
        {
            _nextWater = now.AddMinutes(_s.WaterIntervalMinutes);
            if (_s.WaterCount < _s.WaterGoal) Peek("Time for a glass of water");
        }

        if (_s.ShowWeather) _ = _weather.RefreshAsync(_s); // throttles itself to every 30 minutes

        UpdateHidden();

        if (_expanded)
        {
            UpdateClock();
            if (ReferenceEquals(_currentPanel, PanelSystem)) UpdateSystem(animate: true);
            if (ReferenceEquals(_currentPanel, PanelHome)) UpdateHome();
            if (ReferenceEquals(_currentPanel, PanelCal)) UpdateCalendarSide();
            if (ReferenceEquals(_currentPanel, PanelSound)) UpdateSound(rebuild: false);
        }
        UpdateMini();
    }

    bool _userHidden;

    /// <summary>Set from the tray menu: tucks the notch away until the user asks for it back.</summary>
    public bool UserHidden
    {
        get => _userHidden;
        set
        {
            _userHidden = value;
            UpdateHidden();
        }
    }

    void UpdateHidden()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool fullscreen = _s.HideInFullscreen && !_offscreenTest && Native.IsForegroundFullscreen(_hwnd, ChosenScreen().Bounds);
        bool hide = _userHidden || _cinemaOpen || fullscreen;
        UpdateGameHud(fullscreen && !_userHidden && !_cinemaOpen);
        if (hide != _hiddenForFullscreen) SetHiddenForFullscreen(hide);
        if (!hide) Native.KeepOnTop(_hwnd);
    }

    /// <summary>Slides the notch up and off the screen for full-screen apps, and back down after.</summary>
    void SetHiddenForFullscreen(bool hide)
    {
        if (hide && _expanded) SetExpanded(false);
        _hiddenForFullscreen = hide;
        Island.IsHitTestVisible = !hide;
        if (hide)
        {
            // once it has slid away, take it out of rendering entirely so a game gets the whole CPU
            Fx.Animate(IslandT, TranslateTransform.YProperty, HiddenY, 300, Fx.EaseIn,
                done: () => { if (_hiddenForFullscreen) Shell.Visibility =Visibility.Hidden; });
        }
        else
        {
            Shell.Visibility =Visibility.Visible;
            Fx.Animate(IslandT, TranslateTransform.YProperty, 0, 560, Fx.Spring);
        }
    }

    string Now() => DateTime.Now.ToString(_s.TimeFormat, En);

    void UpdateClock()
    {
        ClockText.Text = Now();
        DateText.Text = DateTime.Now.ToString("ddd, d MMM", En);
    }

    void FollowCursor()
    {
        if (!_s.EyesFollowCursor || !_s.ShowMascot || _hiddenForFullscreen) return;
        if (!Native.GetCursorPos(out var p)) return;
        if (p.X == _lastCursor.X && p.Y == _lastCursor.Y && _expanded == _lastCursorExpanded) return; // nothing moved
        _lastCursor = p;
        _lastCursorExpanded = _expanded;

        var mascot = _expanded ? BigMascot : MiniMascot;
        if (!mascot.IsVisible || PresentationSource.FromVisual(mascot) == null) return;

        var center = mascot.PointToScreen(new Point(mascot.ActualWidth / 2, mascot.ActualHeight / 2));
        mascot.LookAt((p.X - center.X) / 260.0, (p.Y - center.Y) / 200.0);
    }

    /// <summary>Happy right after something good, sad on a dying battery, sleepy late at night.</summary>
    void UpdateMood()
    {
        var mood = MascotMood.Normal;
        if (_s.MascotMoods)
        {
            int hour = DateTime.Now.Hour;
            if (DateTime.Now < _happyUntil) mood = MascotMood.Happy;
            else if (Battery.HasBattery && !Battery.PluggedIn && Battery.Percent <= 15) mood = MascotMood.Sad;
            else if (hour >= 23 || hour < 6) mood = MascotMood.Sleepy;
        }
        MiniMascot.Mood = mood;
        BigMascot.Mood = mood;
    }

    // ---------- collapsed pill ----------

    /// <summary>
    /// Shows a message in the collapsed pill. The default is a proper announcement (chime, glowing
    /// ring, six seconds). "quiet" is for small status popups such as Caps Lock: brief and silent.
    /// </summary>
    void Peek(string text, bool celebrate = true, bool quiet = false, double? seconds = null)
    {
        _peekText = text;
        _peekUntil = DateTime.Now.AddSeconds(seconds ?? (quiet ? 1.8 : 6));
        if (quiet)
        {
            UpdateMini();
            return;
        }
        if (_s.Sounds) System.Media.SystemSounds.Asterisk.Play();
        if (celebrate)
        {
            MiniMascot.Celebrate();
            BigMascot.Celebrate();
            _happyUntil = DateTime.Now.AddSeconds(8);
            UpdateMood();
        }

        // two soft pulses of the accent ring
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.8) };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.18), Fx.EaseOut));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.15, KeyTime.FromPercent(0.5)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.68), Fx.EaseOut));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Glow.BeginAnimation(OpacityProperty, pulse);

        UpdateMini();
    }

    void UpdateMini(bool animate = true)
    {
        bool musicOn = _s.ShowMusic && _media.IsPlaying && _media.Title.Length > 0;
        bool hud = DateTime.Now < _hudUntil;
        string text, kind;
        bool bars = false;
        double width;

        if (hud)
        {
            text = "";
            kind = "hud";
            width = 240;
        }
        else if (DateTime.Now < _peekUntil)
        {
            text = _peekText;
            kind = "peek:" + text;
            width = PillWidthFor(text, bars: false);
        }
        else if (RunningTimerText() is { } timer)
        {
            text = timer;
            kind = "timer:" + timer[..5];
            // sized for the widest digits so the pill doesn't twitch as the seconds change
            width = PillWidthFor(timer[..^5] + "00:00", bars: false);
        }
        else if (musicOn)
        {
            string lyric = _s.ShowLyrics ? _lyrics.LineAt(LyricPosition) : "";
            if (lyric.Length > 0)
            {
                text = lyric;
                kind = "lyric:" + lyric;
            }
            else
            {
                text = _media.Artist.Length > 0 ? $"{_media.Title} · {_media.Artist}" : _media.Title;
                kind = "music:" + text;
            }
            bars = true;
            width = PillWidthFor(text, bars: true); // the pill grows and shrinks to fit each line
        }
        else if (DownloadText() is { } download)
        {
            text = download;
            kind = "download";
            // sized for a steady width so the pill doesn't twitch as the number grows
            width = PillWidthFor("Downloading · 000.0 MB", bars: false);
        }
        else if (_s.MinimalNotch)
        {
            // nothing to say: shrink to just the mascot (or a small blank pill without it)
            text = "";
            kind = "minimal";
            width = _s.ShowMascot ? 64 : 46;
        }
        else
        {
            text = Now();
            kind = "clock";
            width = PillWidthFor(_s.Use24Hour ? "00:00" : "00:00 PM", bars: false);
        }

        // the volume popup takes over the text area while it shows
        if (hud != (MiniHud.Visibility == Visibility.Visible))
        {
            if (hud)
            {
                MiniText.Visibility = Visibility.Collapsed;
                Fx.Appear(MiniHud, dy: 6, ms: 220);
            }
            else
            {
                MiniHud.Visibility = Visibility.Collapsed;
                Fx.Appear(MiniText, dy: 8, ms: 280);
            }
        }

        if (!hud)
        {
            MiniText.Text = text;
            if (kind != _miniKind && animate && _miniKind.Length > 0 && _miniKind != "hud" && !_expanded)
                Fx.Appear(MiniText, dy: 9, ms: 300); // new content slides up into place instead of just swapping
        }
        _miniKind = kind;

        bars &= !hud;
        if (bars && MiniBars.Visibility != Visibility.Visible) Fx.Appear(MiniBars, dx: 6, dy: 0, ms: 260);
        else if (!bars) MiniBars.Visibility = Visibility.Collapsed;
        SetBars(bars);

        bool dance = musicOn && _s.DanceToMusic;
        MiniMascot.Dancing = dance;
        BigMascot.Dancing = dance;
        UpdateKaraokeTimer(); // starts or stops the sing-along fill on the pill's lyric line

        width = Math.Min(MaxMiniWidth, width + PrivacyWidth); // room for the microphone / camera dots

        if (Math.Abs(width - _miniWidth) > 0.5)
        {
            _miniWidth = width;
            if (!_expanded) AnimateIsland(animate);
        }
        else if (!animate)
        {
            AnimateIsland(false);
        }
    }

    /// <summary>
    /// How wide the collapsed pill must be to show this text in full: the measured text plus the
    /// mascot, the music bars, and padding. Very long lines stop at MaxMiniWidth and trim with "…".
    /// </summary>
    double PillWidthFor(string text, bool bars)
    {
        var measured = new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(MiniText.FontFamily, MiniText.FontStyle, MiniText.FontWeight, MiniText.FontStretch),
            MiniText.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double chrome = (_s.ShowMascot ? 5 + 44 : 14)   // left padding + mascot
                        + (bars ? 23 : 0)               // music bars
                        + 14 + 2                        // right padding + border
                        + 22;                           // breathing room around the text
        return Math.Clamp(Math.Ceiling(measured.WidthIncludingTrailingWhitespace) + chrome, MinMiniWidth, MaxMiniWidth);
    }

    BeatEdge _beatEdge;
    int _beatTicks;

    /// <summary>The playing song's tempo, for tests and curiosity.</summary>
    internal double SongBpm => _beatService.Bpm;

    void SetBars(bool on)
    {
        if (on == _barsRunning) return;
        _barsRunning = on;

        if (on)
        {
            _beatService.Start(); // listening only happens while the bars are actually on screen
            _beat.Start();
            return;
        }

        _beat.Stop();
        _beatService.Stop();
        foreach (var bar in new[] { Bar1, Bar2, Bar3 })
            ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>
    /// Moves the three music bars with the actual sound, twenty times a second: the left bar is the
    /// bass (kick drum), the middle the mids (vocals, snare), the right the treble (hi-hats).
    /// </summary>
    void BeatTick()
    {
        double[] target;
        if (_beatService.Running || _beatService.Start())
        {
            var (bass, mid, high) = _beatService.Levels;
            target = new[] { bass, mid, high };
        }
        else
        {
            // no sound device to follow: a gentle made-up wave so the bars still look alive
            double t = Environment.TickCount64 / 1000.0;
            target = new[] { 0.5 + 0.5 * Math.Sin(t * 7.3), 0.5 + 0.5 * Math.Sin(t * 9.1 + 1), 0.5 + 0.5 * Math.Sin(t * 6.2 + 2) };
        }

        // the mascot moves once per beat of the song itself, whatever its tempo
        if (_s.DanceToMusic && _beatService.TryGetBeat(out double phase, out double period))
        {
            if (_beatEdge.Step(phase, period)) (_expanded ? BigMascot : MiniMascot).Beat(period);
        }

        // the timer ticks fast so the mascot lands on the beat; the bars only need every other tick
        if (++_beatTicks % 2 != 0) return;
        var bars = new[] { Bar1, Bar2, Bar3 };
        for (int i = 0; i < bars.Length; i++)
        {
            var a = new DoubleAnimation(0.2 + 0.8 * target[i], TimeSpan.FromMilliseconds(80));
            Timeline.SetDesiredFrameRate(a, 30); // three 3-pixel bars look identical at 30 fps
            ((ScaleTransform)bars[i].RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    static string Format(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    static string FormatShort(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---------- fast tick: things that should move more than once a second ----------

    void UpdateFastTimer()
    {
        bool needed = _media.IsPlaying || _stopwatch.IsRunning || DateTime.Now < _hudUntil;
        if (needed && !_fast.IsEnabled) _fast.Start();
        else if (!needed && _fast.IsEnabled) _fast.Stop();
        UpdateKaraokeTimer(); // the lyrics page has its own, quicker clock
    }

    void FastTick()
    {
        if (_media.IsPlaying)
        {
            UpdateLyric(force: false);
            if (_expanded && ReferenceEquals(_currentPanel, PanelMusic)) UpdateSeek();
        }

        if (_stopwatch.IsRunning && _mode == TimerMode.Stopwatch && _expanded) UpdateTimerUi(animate: false);

        if (_hudUntil != DateTime.MinValue && DateTime.Now >= _hudUntil)
        {
            _hudUntil = DateTime.MinValue;
            UpdateMini();
        }

        UpdateFastTimer();
    }

    // ---------- music ----------

    void UpdateMusic()
    {
        bool on = _s.ShowMusic && _media.HasSession;

        if (on && MusicOn.Visibility != Visibility.Visible)
        {
            MusicOff.Visibility = Visibility.Collapsed;
            Fx.Appear(MusicOn, dy: 8, ms: 300);
        }
        else if (!on && MusicOff.Visibility != Visibility.Visible)
        {
            MusicOn.Visibility = Visibility.Collapsed;
            Fx.Appear(MusicOff, dy: 8, ms: 300);
        }

        if (on)
        {
            string song = _media.Title + "|" + _media.Artist;
            SongTitle.Text = _media.Title.Length > 0 ? _media.Title : "Unknown title";
            SongArtist.Text = _media.Artist;
            ArtBrush.ImageSource = _media.Art;
            ArtBorder.Visibility = _media.Art != null ? Visibility.Visible : Visibility.Collapsed;

            if (song != _lastSong)
            {
                _beatService.NewSong();
                if (_lastSong.Length > 0)
                {
                    // a new track slides in and the cover gives a little bounce
                    Fx.Appear(SongInfo, dx: 14, dy: 0, ms: 340);
                    Fx.Pop(ArtHost, from: 0.86, ms: 420);
                }
                _lastSong = song;

                if (_s.ShowLyrics) _lyrics.Load(_media.Title, _media.Artist, _media.Duration);
                else _lyrics.Clear();
            }

            string icon = _media.IsPlaying ? "" : "";
            if (!Equals(PlayBtn.Content, icon))
            {
                PlayBtn.Content = icon;
                if (_ready) Fx.Pop(PlayBtn, from: 0.7, ms: 320);
            }

            UpdateSeek();
        }
        else if (!_s.ShowMusic)
        {
            _lyrics.Clear();
            _lastSong = "";
        }

        UpdateSongColors();
        UpdateLyric(force: true);
        UpdateFastTimer();
        UpdateMini();
        UpdateLyricsView();
        if (_expanded && ReferenceEquals(_currentPanel, PanelHome)) UpdateHome();
    }

    /// <summary>
    /// While a song with a colorful cover is playing, the notch takes on its color: a glow rises
    /// from the bottom edge, and everything that normally uses the glow color (the mascot's outline,
    /// lyrics, music bars, progress bar, buttons) shifts to a bright version of the same hue.
    /// </summary>
    void UpdateSongColors()
    {
        ApplySongColor(_s.SongColors && _s.ShowMusic && _media.HasSession ? _media.ArtColor : null);
    }

    void ApplySongColor(Color? color)
    {
        if (color == _songColor) return;
        _songColor = color;

        var time = TimeSpan.FromMilliseconds(900);

        if (color is { } c)
        {
            var bright = Palette.Bright(c);
            var deep = Palette.Deep(c);

            FadeAccentTo(bright, release: false);

            TintTop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0, deep.R, deep.G, deep.B), time));
            TintBottom.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(170, deep.R, deep.G, deep.B), time));
            Fx.Animate(Tint, OpacityProperty, 1, 700);
        }
        else
        {
            Fx.Animate(Tint, OpacityProperty, 0, 700);
            // fade back to the user's own glow color, then hand control back to it
            if (_accentOverride) FadeAccentTo(((SolidColorBrush)App.AccentBrush).Color, release: true);
        }
    }

    /// <summary>
    /// Cross-fades the glow color used inside the notch. A brush placed in the notch's resources
    /// overrides the app-wide one for everything inside it, but WPF freezes resource brushes, so
    /// they can't be animated directly: instead a fresh brush is swapped in about 30 times a second.
    /// </summary>
    void FadeAccentTo(Color target, bool release)
    {
        _accentFrom = _accentOverride ? _accentNow : ((SolidColorBrush)App.AccentBrush).Color;
        _accentTo = target;
        _accentRelease = release;
        _accentOverride = true;
        _accentStart = DateTime.Now;

        _accentTimer ??= CreateAccentTimer();
        _accentTimer.Start();
    }

    DispatcherTimer CreateAccentTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Clamp((DateTime.Now - _accentStart).TotalMilliseconds / 800, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);
            byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * eased);
            _accentNow = Color.FromRgb(Mix(_accentFrom.R, _accentTo.R), Mix(_accentFrom.G, _accentTo.G), Mix(_accentFrom.B, _accentTo.B));

            var brush = new SolidColorBrush(_accentNow);
            brush.Freeze();
            Island.Resources["AccentBrush"] = brush;

            if (t < 1) return;
            timer.Stop();
            if (_accentRelease)
            {
                Island.Resources.Remove("AccentBrush");
                _accentOverride = false;
            }
        };
        return timer;
    }

    /// <summary>Shows the line being sung right now, sliding in whenever it changes.</summary>
    void UpdateLyric(bool force)
    {
        bool wanted = _s.ShowLyrics && _s.ShowMusic && _media.HasSession;
        string line = wanted ? _lyrics.LineAt(LyricPosition) : "";

        // say so plainly when a song has no lyrics, rather than leaving an unexplained gap
        bool missing = wanted && !_lyrics.HasLyrics && !_lyrics.Loading;
        if (missing) line = _lyrics.HasPlain ? "Lyrics aren't timed. Open the lyrics page to read them" : "Lyrics not available";

        if (line == _lastLyric && !force) return;

        bool changed = line != _lastLyric;
        _lastLyric = line;
        LyricText.Text = line;
        LyricText.SetResourceReference(TextBlock.ForegroundProperty, missing ? "SubtleBrush" : "AccentBrush");
        LyricText.FontWeight = missing ? FontWeights.Normal : FontWeights.SemiBold;
        if (changed && line.Length > 0 && _expanded) Fx.Appear(LyricText, dy: 7, ms: 280);
        if (changed && !_expanded) UpdateMini(); // the pill shows the lyric too
    }

    void UpdateSeek()
    {
        if (_seeking) return;
        var duration = _media.Duration;
        bool has = duration > TimeSpan.FromSeconds(1);
        SeekRow.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) return;

        var position = _media.Position;
        PosText.Text = FormatShort(position);
        DurText.Text = FormatShort(duration);
        double fraction = Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1);
        // each 200 ms step is itself a 200 ms glide, so the bar moves continuously
        SeekBarS.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fraction, TimeSpan.FromMilliseconds(200)));
    }

    double SeekFraction(MouseEventArgs e)
        => SeekTrack.ActualWidth <= 0 ? 0 : Math.Clamp(e.GetPosition(SeekTrack).X / SeekTrack.ActualWidth, 0, 1);

    void SeekPreview(MouseEventArgs e)
    {
        double fraction = SeekFraction(e);
        SeekBarS.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SeekBarS.ScaleX = fraction;
        PosText.Text = FormatShort(TimeSpan.FromSeconds(_media.Duration.TotalSeconds * fraction));
    }

    void Seek_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _seeking = true;
        SeekTrack.CaptureMouse();
        SeekPreview(e);
        e.Handled = true;
    }

    void Seek_MouseMove(object sender, MouseEventArgs e)
    {
        if (_seeking) SeekPreview(e);
    }

    void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        SeekTrack.ReleaseMouseCapture();
        _media.Seek(TimeSpan.FromSeconds(_media.Duration.TotalSeconds * SeekFraction(e)));
        UpdateLyric(force: true);
    }

    void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();
    void Play_Click(object sender, RoutedEventArgs e) => _media.PlayPause();
    void Next_Click(object sender, RoutedEventArgs e) => _media.Next();

    // ---------- to-do ----------

    void TodoInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            TodoInput.Clear();
            Keyboard.ClearFocus();
            return;
        }
        if (e.Key != Key.Enter) return;

        AddTodo(TodoInput.Text);
        TodoInput.Clear();
    }

    void AddTodo(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        _justAdded = new TodoItem { Text = text };
        _s.Todos.Insert(0, _justAdded);
    }

    /// <summary>A freshly added task grows open and fades in; tasks loaded from disk just appear.</summary>
    void TodoItem_Loaded(object sender, RoutedEventArgs e)
    {
        var row = (FrameworkElement)sender;
        if (!ReferenceEquals(row.DataContext, _justAdded)) return;
        _justAdded = null;

        double height = row.ActualHeight;
        var grow = new DoubleAnimation(0, height, TimeSpan.FromMilliseconds(260)) { EasingFunction = Fx.EaseOut };
        grow.Completed += (_, _) => row.BeginAnimation(HeightProperty, null);
        row.BeginAnimation(HeightProperty, grow);
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
    }

    void TodoCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: true, DataContext: TodoItem item }) CompleteTodo(item);
    }

    void CompleteTodo(TodoItem item)
    {
        BigMascot.Celebrate();
        _happyUntil = DateTime.Now.AddSeconds(6);
        UpdateMood();
        if (item.Rewarded) return;
        item.Rewarded = true;
        AddXp(10);
    }

    void TodoDelete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TodoItem item) RemoveTodo(item);
    }

    void RemoveTodo(TodoItem item)
    {
        if (TodoList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement row)
        {
            _s.Todos.Remove(item);
            return;
        }

        // fade, then fold the row shut so the list closes up smoothly
        row.IsHitTestVisible = false;
        row.ClipToBounds = true;
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        var fold = new DoubleAnimation(row.ActualHeight, 0, TimeSpan.FromMilliseconds(240))
        {
            BeginTime = TimeSpan.FromMilliseconds(80),
            EasingFunction = Fx.EaseOut,
        };
        fold.Completed += (_, _) => _s.Todos.Remove(item);
        row.BeginAnimation(HeightProperty, fold);
    }

    // ---------- water ----------

    void RollWaterDay()
    {
        if (_s.WaterDate == AppSettings.Today) return;
        _s.WaterDate = AppSettings.Today;
        _s.WaterCount = 0;
    }

    void UpdateWater()
    {
        WaterLabel.Text = $"{_s.WaterCount} of {_s.WaterGoal} glasses today";

        var empty = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        WaterDrops.Children.Clear();
        for (int i = 0; i < _s.WaterGoal; i++)
        {
            var drop = new Path
            {
                Data = DropShape,
                Width = 13,
                Height = 19,
                Stretch = Stretch.Fill,
                Margin = new Thickness(0, 0, 7, 0),
                Fill = empty,
            };
            if (i < _s.WaterCount) drop.SetResourceReference(Shape.FillProperty, "AccentBrush");
            WaterDrops.Children.Add(drop);

            // the drop that was just filled pops
            if (_lastWaterCount >= 0 && _s.WaterCount > _lastWaterCount && i == _s.WaterCount - 1)
                Fx.Pop(drop, from: 0.3, ms: 460);
        }

        _lastWaterCount = _s.WaterCount;
    }

    void WaterAdd_Click(object sender, RoutedEventArgs e)
    {
        bool reachedGoal = _s.WaterCount + 1 == _s.WaterGoal;
        _s.WaterCount++;
        _nextWater = DateTime.Now.AddMinutes(_s.WaterIntervalMinutes);

        if (reachedGoal) Peek("Water goal reached. Nice one");
        else BigMascot.Celebrate();
        AddXp(reachedGoal ? 20 : 5);
    }

    void WaterUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_s.WaterCount == 0) return;
        _s.WaterCount--;
        _s.Xp -= 5;
    }
}
