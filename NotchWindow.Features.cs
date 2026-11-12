using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Piztki.Core;

namespace Piztki;

// Timers, levels, the volume popup, battery alerts, weather, the shelf, clipboard history, and system stats.
public partial class NotchWindow
{
    enum PomoState { Idle, Focus, Break }
    enum TimerMode { Focus, Timer, Stopwatch }

    static readonly TimeSpan DefaultCountdown = TimeSpan.FromMinutes(5);

    readonly WeatherService _weather = new();
    readonly SystemStats _stats = new();
    readonly VolumeReader _volume = new();
    readonly Stopwatch _stopwatch = new();
    readonly List<string> _clips = new();

    TimerMode _mode = TimerMode.Focus;

    PomoState _pomo = PomoState.Idle;
    bool _pomoRunning;
    TimeSpan _pomoLeft;

    bool _countdownRunning;
    TimeSpan _countdownLeft = DefaultCountdown;
    TimeSpan _countdownTotal = DefaultCountdown;

    double _lastVolume = -1;
    bool _lastMuted;
    DateTime _hudUntil = DateTime.MinValue;

    bool? _lastPluggedIn;
    bool _lowBatteryWarned;

    DateTime _dragOverAt = DateTime.MinValue;
    string? _ownClip;

    // =====================================================================
    // Timers: focus sessions, a plain countdown, and a stopwatch share one panel
    // =====================================================================

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _mode = ReferenceEquals(sender, ModeTimer) ? TimerMode.Timer
            : ReferenceEquals(sender, ModeStopwatch) ? TimerMode.Stopwatch
            : ReferenceEquals(sender, ModeStats) ? _mode // the chart is a view, not a timer: whatever was running keeps running
            : TimerMode.Focus;

        bool stats = ReferenceEquals(sender, ModeStats);
        TimerBody.Visibility = stats ? Visibility.Collapsed : Visibility.Visible;
        StatsBody.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
        if (stats)
        {
            RebuildStats(animate: true);
            Fx.Appear(StatsBody, dy: 8, ms: 280);
            return;
        }
        UpdateTimerUi(animate: false);
        Fx.Appear(TimerBody, dy: 8, ms: 280);
        UpdateFastTimer();
    }

    void TickTimers()
    {
        if (_pomoRunning)
        {
            _pomoLeft -= TimeSpan.FromSeconds(1);

            // "focused today": counted in memory and written once a minute, not once a second
            if (_pomo == PomoState.Focus)
            {
                if (_s.FocusDate != AppSettings.Today)
                {
                    _s.FocusDate = AppSettings.Today;
                    _s.FocusSecondsToday = 0;
                    _focusUnsaved = 0;
                }
                if (++_focusUnsaved >= 60)
                {
                    RecordFocusDay(_s.FocusSecondsToday + _focusUnsaved); // the weekly chart's copy, saved by the line below
                    _s.FocusSecondsToday += _focusUnsaved;
                    _focusUnsaved = 0;
                }
            }

            if (_pomoLeft <= TimeSpan.Zero)
            {
                if (_pomo == PomoState.Focus)
                {
                    _pomo = PomoState.Break;
                    _pomoLeft = TimeSpan.FromMinutes(_s.BreakMinutes);
                    _pomoRunning = _s.AutoStartBreak;
                    Peek("Focus done. Take a break");
                    AddXp(25);
                }
                else
                {
                    _pomo = PomoState.Idle;
                    _pomoRunning = false;
                    _pomoLeft = TimeSpan.FromMinutes(_s.FocusMinutes);
                    Peek("Break's over. Ready for another?");
                }
                if (_mode == TimerMode.Focus) Fx.Pop(PomoTime, from: 0.85);
            }
        }

        if (_countdownRunning)
        {
            _countdownLeft -= TimeSpan.FromSeconds(1);
            if (_countdownLeft <= TimeSpan.Zero)
            {
                _countdownRunning = false;
                _countdownLeft = _countdownTotal = DefaultCountdown;
                Peek("Time's up");
                if (_mode == TimerMode.Timer) Fx.Pop(PomoTime, from: 0.85);
            }
        }

        if (_mode != TimerMode.Stopwatch) UpdateTimerUi(animate: true);
    }

    /// <summary>What the collapsed pill shows while a timer runs, or null when none is running.</summary>
    string? RunningTimerText()
    {
        if (!_s.ShowFocus) return null;
        if (_pomoRunning) return (_pomo == PomoState.Break ? "Break  " : "Focus  ") + Format(_pomoLeft);
        if (_countdownRunning) return "Timer  " + Format(_countdownLeft);
        if (_stopwatch.IsRunning) return "Stopwatch  " + Format(_stopwatch.Elapsed);
        return null;
    }

    void UpdateTimerUi(bool animate)
    {
        string label, time, start;
        double progress;
        bool running;

        switch (_mode)
        {
            case TimerMode.Timer:
                running = _countdownRunning;
                bool freshTimer = _countdownLeft == _countdownTotal;
                label = running ? "Counting down" : freshTimer ? "Set a timer" : "Timer paused";
                time = Format(_countdownLeft);
                start = running ? "Pause" : freshTimer ? "Start" : "Resume";
                progress = _countdownTotal.TotalSeconds <= 0 || freshTimer ? 0
                    : 1 - _countdownLeft.TotalSeconds / _countdownTotal.TotalSeconds;
                break;

            case TimerMode.Stopwatch:
                running = _stopwatch.IsRunning;
                var elapsed = _stopwatch.Elapsed;
                label = running ? "Timing" : elapsed > TimeSpan.Zero ? "Stopwatch paused" : "Stopwatch";
                time = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100}";
                start = running ? "Pause" : elapsed > TimeSpan.Zero ? "Resume" : "Start";
                progress = 0;
                break;

            default:
                running = _pomoRunning;
                double total = TimeSpan.FromMinutes(_pomo == PomoState.Break ? _s.BreakMinutes : _s.FocusMinutes).TotalSeconds;
                bool fresh = Math.Abs(_pomoLeft.TotalSeconds - total) < 0.5;
                label = _pomo switch
                {
                    PomoState.Focus => running ? "Focusing" : "Focus paused",
                    PomoState.Break => running ? "On a break" : fresh ? "Break time" : "Break paused",
                    _ => FocusedTodayMinutes > 0 ? $"Ready to focus · {FocusedTodayMinutes} min today" : "Ready to focus",
                };
                time = Format(_pomoLeft);
                start = running ? "Pause"
                    : _pomo == PomoState.Idle ? "Start"
                    : _pomo == PomoState.Break && fresh ? "Start break"
                    : "Resume";
                progress = _pomo == PomoState.Idle || total <= 0 ? 0 : 1 - _pomoLeft.TotalSeconds / total;
                break;
        }

        PomoLabel.Text = label;
        PomoTime.Text = time;
        PomoStart.Content = start;
        QuickAdd.Visibility = _mode == TimerMode.Timer ? Visibility.Visible : Visibility.Collapsed;

        progress = Math.Clamp(progress, 0, 1);
        if (animate && running)
        {
            // the bar glides continuously: each one-second step is itself a one-second linear animation
            PomoBarS.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(progress, TimeSpan.FromSeconds(1)));
        }
        else if (!animate || Math.Abs(PomoBarS.ScaleX - progress) > 0.001)
        {
            Fx.Animate(PomoBarS, ScaleTransform.ScaleXProperty, progress, 320);
        }
    }

    void PomoStart_Click(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case TimerMode.Timer:
                _countdownRunning = !_countdownRunning;
                break;
            case TimerMode.Stopwatch:
                if (_stopwatch.IsRunning) _stopwatch.Stop();
                else _stopwatch.Start();
                break;
            default:
                if (_pomo == PomoState.Idle)
                {
                    _pomo = PomoState.Focus;
                    _pomoLeft = TimeSpan.FromMinutes(_s.FocusMinutes);
                    _pomoRunning = true;
                }
                else
                {
                    _pomoRunning = !_pomoRunning;
                }
                break;
        }
        AfterTimerChange();
    }

    void PomoReset_Click(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case TimerMode.Timer:
                _countdownRunning = false;
                _countdownLeft = _countdownTotal = DefaultCountdown;
                break;
            case TimerMode.Stopwatch:
                _stopwatch.Reset();
                break;
            default:
                _pomo = PomoState.Idle;
                _pomoRunning = false;
                _pomoLeft = TimeSpan.FromMinutes(_s.FocusMinutes);
                break;
        }
        AfterTimerChange();
    }

    void QuickAdd_Click(object sender, RoutedEventArgs e)
    {
        var extra = TimeSpan.FromMinutes(int.Parse((string)((FrameworkElement)sender).Tag));
        if (_countdownLeft + extra > TimeSpan.FromMinutes(180)) return;
        _countdownLeft += extra;
        _countdownTotal += extra;
        AfterTimerChange();
    }

    void AfterTimerChange()
    {
        UpdateTimerUi(animate: false);
        Fx.Pop(PomoTime, from: 0.9, ms: 300);
        UpdateFastTimer();
        UpdateMini();
    }

    // =====================================================================
    // Home: one small card per feature, so nothing hides behind an icon
    // =====================================================================

    readonly Dictionary<string, (TextBlock value, TextBlock sub)> _homeCards = new();

    void RebuildHome()
    {
        HomeItems.Children.Clear();
        _homeCards.Clear();
        SearchRow.Visibility = _s.ShowSearch ? Visibility.Visible : Visibility.Collapsed;
        if (!_s.ShowSearch && SearchBox.Text.Length > 0) SearchBox.Clear();

        var iconFont = (FontFamily)FindResource("Icons");
        var subtle = (Brush)FindResource("SubtleBrush");

        void Card(string key, object icon, string label, RadioButton? tab, double width, Action? click = null)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon is string glyph)
            {
                var glyphText = new TextBlock { Text = glyph, FontFamily = iconFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                glyphText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                head.Children.Add(glyphText);
            }
            else if (icon is UIElement element)
            {
                head.Children.Add(element);
            }
            head.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = subtle, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

            var value = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
            var sub = new TextBlock { FontSize = 10.5, Foreground = subtle, TextTrimming = TextTrimming.CharacterEllipsis };

            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(value);
            body.Children.Add(sub);

            var card = new Button { Style = (Style)FindResource("HomeCard"), Width = width, Content = body, ToolTip = "Open " + label };
            card.Click += (_, _) =>
            {
                if (click != null)
                {
                    click();
                    return;
                }
                tab!.IsChecked = true;
                if (key == "event") OpenNextEventDay(); // land on the day of that event, not just the tab
            };
            HomeItems.Children.Add(card);
            _homeCards[key] = (value, sub);
        }

        var drop = new System.Windows.Shapes.Path { Data = DropShape, Width = 8, Height = 11, Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Center };
        drop.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");

        // a download comes first while it runs and for a few minutes after: it is the newest news
        _downloadCardShown = DownloadCardWanted;
        if (_downloadCardShown) Card("download", "\uE896", "Downloads", null, 378, OpenLastDownload);

        if (_s.MatchSummary && _s.LastMatchHead.Length > 0) Card("match", GamePicture() ?? (object)"\uE7FC", _s.LastMatchGame + (_s.LastMatchGame is "Fortnite" or "Counter-Strike 2" ? " · last match" : _s.LastMatchGame == "Roblox" && _robloxPlaying ? " · playing now" : " · last game"), null, 186, ShowLastMatch);

        // music gets a double-width card: song titles need the room
        if (_s.ShowMusic) Card("music", "", "Music", TabMusic, 186);
        // the soonest calendar event sits right next to the music, where it's hard to miss
        if (_s.ShowCalendar) Card("event", "", "Next event", TabCal, 186);
        if (_s.ShowFocus) Card("timer", "", "Timers", TabFocus, 90);
        if (_s.ShowTodo) Card("todo", "", "Tasks", TabTodo, 90);
        if (_s.ShowWater) Card("water", drop, "Water", TabWater, 90);
        if (_s.ShowShelf) Card("shelf", "", "Shelf", TabShelf, 90);
        if (_s.ShowClipboard) Card("clip", "", "Copied", TabClip, 90);
        if (_s.ShowSystem) Card("system", "", "System", TabSystem, 90);
        if (_s.ShowSystem) Card("net", "\uE774", "Internet", TabSystem, 90);
        if (_s.ShowNotes) Card("notes", "\uE70B", "Notes", TabNotes, 90);
        if (_s.ShowShortcuts) Card("shortcuts", "\uE718", "Shortcuts", TabShortcuts, 90);
        if (_s.ShowSound) Card("sound", "\uE767", "Sound", TabSound, 90);

        UpdateHome();
    }

    /// <summary>The picture of the last game for its card, when there is one saved on this PC.</summary>
    UIElement? GamePicture()
    {
        try
        {
            if (_s.LastMatchIcon.Length == 0 || !System.IO.File.Exists(_s.LastMatchIcon)) return null;
            var picture = new System.Windows.Media.Imaging.BitmapImage();
            picture.BeginInit();
            picture.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // don't keep the file open
            picture.DecodePixelWidth = 48;
            picture.UriSource = new Uri(_s.LastMatchIcon);
            picture.EndInit();
            picture.Freeze();
            return new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
                Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
            };
        }
        catch
        {
            return null; // a broken download: the usual icon is fine
        }
    }

    void UpdateHome()
    {
        void Set(string key, string value, string sub)
        {
            if (!_homeCards.TryGetValue(key, out var card)) return;
            card.value.Text = value;
            card.sub.Text = sub;
        }

        Set("music",
            _media.HasSession ? (_media.Title.Length > 0 ? _media.Title : "Unknown title") : "Nothing playing",
            _media.HasSession ? (_media.IsPlaying ? _media.Artist : "Paused") : "Play a song anywhere");

        if (_pomoRunning || _pomo != PomoState.Idle)
            Set("timer", Format(_pomoLeft), _pomo == PomoState.Break ? "Break" : _pomoRunning ? "Focusing" : "Paused");
        else if (_countdownRunning)
            Set("timer", Format(_countdownLeft), "Timer");
        else if (_stopwatch.IsRunning)
            Set("timer", Format(_stopwatch.Elapsed), "Stopwatch");
        else
            Set("timer", Format(TimeSpan.FromMinutes(_s.FocusMinutes)), FocusedTodayMinutes > 0 ? $"{FocusedTodayMinutes} min today" : "Ready");

        var nextEvent = NextEvent();
        Set("event", nextEvent?.Title ?? "Nothing coming up", nextEvent != null ? WhenText(nextEvent.When) : "Add an event in the calendar");

        int open = _s.Todos.Count(t => !t.Done);
        var next = _s.Todos.FirstOrDefault(t => !t.Done);
        Set("todo", _s.Todos.Count == 0 ? "None" : open == 0 ? "All done" : $"{open} left", next?.Text ?? (_s.Todos.Count == 0 ? "Add a task" : "Nice work"));

        Set("water", $"{_s.WaterCount} / {_s.WaterGoal}", "glasses");
        Set("shelf", _s.Shelf.Count == 0 ? "Empty" : _s.Shelf.Count.ToString(), _s.Shelf.Count == 1 ? "file" : _s.Shelf.Count == 0 ? "Drop files" : "files");
        Set("clip", _clips.Count == 0 ? "Empty" : _clips.Count.ToString(), _clips.Count == 0 ? "Copy text" : _clips.Count == 1 ? "item" : "items");

        UpdateToolCards(Set);
        Set("match", _s.LastMatchHead, _s.LastMatchDetail);

        if (_homeCards.ContainsKey("system"))
        {
            _stats.Update();
            Set("system", $"{Math.Round(_stats.Cpu * 100)}%", "CPU");
            Set("net", _offline ? "Offline" : SystemStats.Speed(_stats.DownBytesPerSec), _offline ? "No connection" : "up " + SystemStats.Speed(_stats.UpBytesPerSec));
        }
    }

    // =====================================================================
    // Levels
    // =====================================================================

    void AddXp(int amount)
    {
        int before = _s.Level;
        _s.Xp += amount; // UpdateLevel runs through the settings change
        int after = _s.Level;
        if (after > before && _s.ShowLevel && _s.ShowMascot) Peek($"Level {after}. Piztki is growing");
    }

    void UpdateLevel(bool animate)
    {
        LevelText.Text = "Lv " + _s.Level;
        if (animate) Fx.Animate(LevelBarS, ScaleTransform.ScaleXProperty, _s.LevelProgress, 500);
        else
        {
            LevelBarS.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            LevelBarS.ScaleX = _s.LevelProgress;
        }
    }

    // =====================================================================
    // Volume popup
    // =====================================================================

    void PollVolume()
    {
        if (!_s.VolumeHud)
        {
            _lastVolume = -1;
            return;
        }
        if (!_volume.TryRead(out double level, out bool muted)) return;

        bool first = _lastVolume < 0;
        bool changed = Math.Abs(level - _lastVolume) > 0.004 || muted != _lastMuted;
        _lastVolume = level;
        _lastMuted = muted;
        if (first || !changed) return; // the first reading is only a baseline
        ShowVolume(level, muted);
    }

    void ShowVolume(double level, bool muted)
    {
        HudIcon.Text = muted || level < 0.005 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
        HudText.Text = muted ? "Mute" : $"{Math.Round(level * 100)}%";
        Fx.Animate(HudBarS, ScaleTransform.ScaleXProperty, muted ? 0 : level, 140);

        _hudUntil = DateTime.Now.AddSeconds(1.6);
        UpdateFastTimer();
        UpdateMini();
    }

    // =====================================================================
    // Battery
    // =====================================================================

    void TickBattery()
    {
        if (!Battery.HasBattery) return;

        bool plugged = Battery.PluggedIn;
        int percent = Battery.Percent;

        if (_s.BatteryAlerts)
        {
            if (_lastPluggedIn is { } was && was != plugged)
                Peek(plugged ? $"Charging · {percent}%" : $"On battery · {percent}%", celebrate: plugged);

            if (!plugged && percent <= 20 && !_lowBatteryWarned)
            {
                _lowBatteryWarned = true;
                Peek($"Battery low · {percent}%", celebrate: false);
            }
        }

        if (plugged || percent > 25) _lowBatteryWarned = false;
        _lastPluggedIn = plugged;
    }

    // =====================================================================
    // Weather
    // =====================================================================

    void UpdateWeather()
    {
        WeatherText.Text = _weather.Text;
        bool show = _s.ShowWeather && _weather.Text.Length > 0;
        if (show && WeatherText.Visibility != Visibility.Visible) Fx.Appear(WeatherText, dy: 5, ms: 300);
        else if (!show) WeatherText.Visibility = Visibility.Collapsed;
    }

    public string WeatherStatus => _weather.Status;
    public event Action? WeatherStatusChanged
    {
        add => _weather.Changed += value;
        remove => _weather.Changed -= value;
    }

    // =====================================================================
    // System stats
    // =====================================================================

    void UpdateSystem(bool animate)
    {
        _stats.Update();
        int ms = animate ? 700 : 1;

        Fx.Animate(CpuBarS, ScaleTransform.ScaleXProperty, _stats.Cpu, ms);
        CpuText.Text = $"{Math.Round(_stats.Cpu * 100)}%";

        Fx.Animate(MemBarS, ScaleTransform.ScaleXProperty, _stats.Memory, ms);
        MemText.Text = $"{_stats.MemoryUsedGb:0.0} / {_stats.MemoryTotalGb:0} GB";

        NetDownText.Text = SystemStats.Speed(_stats.DownBytesPerSec);
        NetUpText.Text = SystemStats.Speed(_stats.UpBytesPerSec);

        if (Battery.HasBattery)
        {
            BatteryRow.Visibility = Visibility.Visible;
            Fx.Animate(BatBarS, ScaleTransform.ScaleXProperty, Battery.Percent / 100.0, ms);
            BatText.Text = Battery.PluggedIn ? $"{Battery.Percent}% charging" : $"{Battery.Percent}%";
        }
        else
        {
            BatteryRow.Visibility = Visibility.Collapsed;
        }
    }

    // =====================================================================
    // Shelf: park files on the notch, drag them back out later
    // =====================================================================

    const int ShelfMax = 12;

    /// <summary>A file dragged in while the Shortcuts tab is open gets pinned there instead of parked on the shelf.</summary>
    bool DropPinsShortcut => _s.ShowShortcuts && _expanded && ReferenceEquals(_currentPanel, PanelShortcuts);

    bool AcceptsDrop(DragEventArgs e) => (_s.ShowShelf || DropPinsShortcut) && e.Data.GetDataPresent(DataFormats.FileDrop);

    void Island_DragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!AcceptsDrop(e))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        _dragOverAt = DateTime.Now;
        _collapse.Stop();
        bool pin = DropPinsShortcut;
        SetExpanded(true);
        if (!pin) TabShelf.IsChecked = true;
    }

    void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = AcceptsDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        if (e.Effects != DragDropEffects.None) _dragOverAt = DateTime.Now;
    }

    void Island_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when the pointer just moves between parts of the notch;
        // the collapse timer waits and checks whether a drag is still hovering
        _collapse.Stop();
        _collapse.Start();
    }

    void Island_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!AcceptsDrop(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (DropPinsShortcut) AddShortcuts(paths);
        else AddToShelf(paths);
        _collapse.Stop();
        _collapse.Start();
    }

    void AddToShelf(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            if (_s.Shelf.Count >= ShelfMax) break;
            if (_s.Shelf.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            _s.Shelf.Add(path);
            added++;
        }
        if (added == 0) return;

        RebuildShelf(popLast: added);
        BigMascot.Celebrate();
    }

    void RebuildShelf(int popLast = 0)
    {
        ShelfItems.Children.Clear();
        for (int i = 0; i < _s.Shelf.Count; i++)
        {
            var chip = CreateShelfChip(_s.Shelf[i]);
            ShelfItems.Children.Add(chip);
            if (i >= _s.Shelf.Count - popLast) Fx.Pop(chip, from: 0.5, ms: 420, delay: (i - (_s.Shelf.Count - popLast)) * 60);
        }

        bool any = _s.Shelf.Count > 0;
        ShelfEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ShelfScroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement CreateShelfChip(string path)
    {
        bool exists = File.Exists(path) || Directory.Exists(path);
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (name.Length == 0) name = path;

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        ImageSource? icon = exists ? ShellIcon(path) : null;
        if (icon != null)
        {
            row.Children.Add(new Image { Source = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            row.Children.Add(new TextBlock
            {
                Text = Directory.Exists(path) ? "" : "",
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            MaxWidth = 92,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var remove = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "",
            Width = 18,
            Height = 18,
            FontSize = 8,
            Foreground = (Brush)FindResource("SubtleBrush"),
            ToolTip = "Take off the shelf",
        };
        row.Children.Add(remove);

        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 4, 3, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            Child = row,
            Opacity = exists ? 1 : 0.45,
            ToolTip = exists ? path : "This file was moved or deleted",
        };

        remove.Click += (_, _) =>
        {
            Fx.Disappear(chip, dy: -6, ms: 150, done: () =>
            {
                _s.Shelf.Remove(path);
                RebuildShelf();
            });
        };

        // click opens the file; press and pull drags it out as a real file
        Point? pressedAt = null;
        chip.MouseLeftButtonDown += (_, e) => pressedAt = e.GetPosition(chip);
        chip.MouseLeave += (_, _) => { if (Mouse.LeftButton != MouseButtonState.Pressed) pressedAt = null; };
        chip.MouseMove += (_, e) =>
        {
            if (pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed || !exists) return;
            var now = e.GetPosition(chip);
            if (Math.Abs(now.X - start.X) < 5 && Math.Abs(now.Y - start.Y) < 5) return;
            pressedAt = null;
            DragDrop.DoDragDrop(chip, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Link);
        };
        chip.MouseLeftButtonUp += (_, _) =>
        {
            if (pressedAt == null || !exists) return;
            pressedAt = null;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        };

        return chip;
    }

    static ImageSource? ShellIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null; // folders fall back to the glyph
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    // =====================================================================
    // Clipboard history (kept in memory only, gone when Piztki quits)
    // =====================================================================

    const int WM_CLIPBOARDUPDATE = 0x031D;
    int _focusUnsaved;

    /// <summary>Whole minutes spent in focus sessions today.</summary>
    int FocusedTodayMinutes => _s.FocusDate == AppSettings.Today ? (_s.FocusSecondsToday + _focusUnsaved) / 60 : 0;

    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);

    void StartClipboardWatch()
    {
        if (!AddClipboardFormatListener(_hwnd)) return;
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            // read a moment later: the app that copied may still be holding the clipboard open
            if (msg == WM_CLIPBOARDUPDATE) Dispatcher.BeginInvoke(OnClipboardChanged);
            return IntPtr.Zero;
        });
    }

    void OnClipboardChanged()
    {
        if (!_s.ShowClipboard) return;

        string text;
        try
        {
            // password managers set this marker to ask history tools not to record what they copy
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return;
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch
        {
            return; // another app has the clipboard locked right now
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > 20000) return;
        AddClip(text);
    }

    void AddClip(string text)
    {
        bool own = text == _ownClip;
        _ownClip = null;

        _clips.Remove(text);
        _clips.Insert(0, text);
        while (_clips.Count > ClipMax) _clips.RemoveAt(_clips.Count - 1);
        RebuildClip(popFirst: !own);
        SaveClips();
    }

    void RebuildClip(bool popFirst = false)
    {
        ClipItems.Children.Clear();
        foreach (var clip in _clips)
        {
            string full = clip;
            string oneLine = string.Join(" ", clip.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (oneLine.Length > 90) oneLine = oneLine[..90];

            var row = new Button
            {
                Style = (Style)FindResource("RowButton"),
                Content = new TextBlock { Text = oneLine, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = "Copy again",
            };
            row.Click += (_, _) =>
            {
                try
                {
                    _ownClip = full;
                    Clipboard.SetText(full);
                    Fx.Pop(row, from: 0.94, ms: 300);
                }
                catch
                {
                    _ownClip = null;
                }
            };
            ClipItems.Children.Add(row);
        }

        if (popFirst && ClipItems.Children.Count > 0) Fx.Appear(ClipItems.Children[0], dy: -8, ms: 300);

        bool any = _clips.Count > 0;
        ClipEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ClipFilled.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    void ClipClear_Click(object sender, RoutedEventArgs e)
    {
        _clips.Clear();
        RebuildClip();
        Fx.Appear(ClipEmpty, dy: 8, ms: 280);
        SaveClips();
    }
}
