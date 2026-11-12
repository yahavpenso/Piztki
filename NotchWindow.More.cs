using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Piztki.Core;

namespace Piztki;

// Which screen the notch sits on, the small status popups (Caps Lock, keyboard language, internet),
// the calendar tab, and remembering the clipboard list.
public partial class NotchWindow
{
    bool _lastCaps;
    int _lastLanguage;
    DateTime _calMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1);
    string _calBuiltFor = "";

    // =====================================================================
    // Screen
    // =====================================================================

    bool _cinemaOpen;

    /// <summary>While the fullscreen lyrics view is open, the notch slides away so it doesn't sit on top of it.</summary>
    internal bool CinemaOpen
    {
        get => _cinemaOpen;
        set
        {
            _cinemaOpen = value;
            UpdateHidden();
        }
    }

    internal System.Windows.Forms.Screen ChosenScreen()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        return screens.FirstOrDefault(s => s.DeviceName == _s.ScreenName)
               ?? System.Windows.Forms.Screen.PrimaryScreen
               ?? screens[0];
    }

    /// <summary>Puts the notch at the top center of the screen the user picked (the main one by default).</summary>
    void MoveToChosenScreen(bool animate)
    {
        if (_hwnd == IntPtr.Zero) return;
        var bounds = ChosenScreen().Bounds;

        if (!animate || _hiddenForFullscreen)
        {
            Native.MoveToTopCenter(_hwnd, bounds);
            return;
        }

        // slide up and out on the old screen, then drop in on the new one
        SetExpanded(false);
        Fx.Animate(IslandT, TranslateTransform.YProperty, HiddenY, 260, Fx.EaseIn, done: () =>
        {
            Native.MoveToTopCenter(_hwnd, bounds);
            Fx.Animate(IslandT, TranslateTransform.YProperty, 0, 560, Fx.Spring, delay: 80);
        });
    }

    // =====================================================================
    // Small status popups
    // =====================================================================

    void PollKeys()
    {
        bool caps = Native.CapsLockOn;
        if (caps != _lastCaps)
        {
            _lastCaps = caps;
            if (_s.HudCapsLock) Peek(caps ? "Caps Lock on" : "Caps Lock off", quiet: true);
        }

        int language = Native.KeyboardLanguage;
        if (language != 0 && language != _lastLanguage)
        {
            _lastLanguage = language;
            if (_s.HudLanguage) Peek("Keyboard: " + LanguageName(language), quiet: true);
        }
    }

    static string LanguageName(int languageId)
    {
        try
        {
            var culture = new CultureInfo(languageId);
            var language = culture.IsNeutralCulture ? culture : culture.Parent;
            // the language written in itself, the way Windows shows it ("עברית", "English")
            string name = language.NativeName;
            return name.Length > 0 ? char.ToUpper(name[0], culture) + name[1..] : culture.TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch
        {
            return "changed";
        }
    }

    void WatchNetwork()
    {
        bool online = NetworkInterface.GetIsNetworkAvailable();
        SetOffline(!online);
        NetworkChange.NetworkAvailabilityChanged += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (e.IsAvailable == online) return;
            online = e.IsAvailable;
            SetOffline(!online);
            if (_s.HudNetwork) Peek(online ? "Back online" : "No internet connection", quiet: true);
        });
    }

    // =====================================================================
    // Calendar tab
    // =====================================================================

    void CalPrev_Click(object sender, RoutedEventArgs e) => ShiftMonth(-1);
    void CalNext_Click(object sender, RoutedEventArgs e) => ShiftMonth(1);

    void ShiftMonth(int by)
    {
        _calMonth = _calMonth.AddMonths(by);
        RebuildCalendar();
        Fx.Appear(CalGrid, dx: by * 14, dy: 0, ms: 260);
    }

    DateTime _calSelected = DateTime.Now.Date;
    int _calVersion; // bumped whenever events or the selected day change, so the grid redraws

    /// <summary>True while the user is typing in any box inside the notch, so it must stay open.</summary>
    bool TypingInNotch => Keyboard.FocusedElement is TextBox box && box.IsDescendantOf(Island);

    void InitCalendar()
    {
        EventTitleBox.TextChanged += (_, _) =>
            EventTitleHint.Visibility = EventTitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildEvents();
    }

    void SelectDay(DateTime day)
    {
        _calSelected = day.Date;
        _calVersion++;
        RebuildCalendar();
        RebuildEvents();
        Fx.Appear(EventItems, dx: 10, dy: 0, ms: 240);
    }

    void CalView_Click(object sender, RoutedEventArgs e)
        => ShowCalendarSide(world: CalWorld.Visibility != Visibility.Visible, animate: true);

    /// <summary>The right half of the calendar tab shows either the day's events or the world clocks.</summary>
    void ShowCalendarSide(bool world, bool animate)
    {
        CalEvents.Visibility = world ? Visibility.Collapsed : Visibility.Visible;
        CalWorld.Visibility = world ? Visibility.Visible : Visibility.Collapsed;
        var shown = world ? (UIElement)CalWorld : CalEvents;
        if (animate) Fx.Appear(shown, dx: world ? 14 : -14, dy: 0, ms: 280);
        else Fx.Appear(shown, dx: 0, dy: 0, ms: 1);
        UpdateCalendarSide();
        if (!world) RebuildEvents();
    }

    /// <summary>Opens the calendar on the month and day of the next event (today if there is none).</summary>
    void OpenNextEventDay()
    {
        var day = NextEvent()?.When.Date ?? DateTime.Now.Date;
        _calMonth = new DateTime(day.Year, day.Month, 1);
        ShowCalendarSide(world: false, animate: false);
        SelectDay(day);
    }

    /// <summary>The soonest event that hasn't started yet, or null.</summary>
    CalEvent? NextEvent()
    {
        var now = DateTime.Now;
        return _s.Events.Where(ev => ev.When >= now).OrderBy(ev => ev.When).FirstOrDefault();
    }

    /// <summary>"Today 14:00", "Tomorrow 09:00", or "Sat 14 Nov 08:00".</summary>
    static string WhenText(DateTime when)
    {
        int days = (when.Date - DateTime.Now.Date).Days;
        string day = days == 0 ? "Today" : days == 1 ? "Tomorrow" : when.ToString(days < 7 ? "dddd" : "ddd d MMM", En);
        return $"{day} {when:HH:mm}";
    }

    // ----- events -----

    void RebuildEvents()
    {
        var today = DateTime.Now.Date;
        int away = (_calSelected - today).Days;
        DayTitle.Text = away == 0 ? "Today" : away == 1 ? "Tomorrow" : _calSelected.ToString("ddd, d MMM", En);

        // the time box suggests something sensible: the next full hour today, otherwise 9 in the morning
        if (!EventTimeBox.IsKeyboardFocused)
            EventTimeBox.Text = away == 0 ? $"{Math.Min(DateTime.Now.Hour + 1, 23):00}:00" : "09:00";

        EventItems.Children.Clear();
        var subtle = (Brush)FindResource("SubtleBrush");
        var dayEvents = _s.Events.Where(ev => ev.When.Date == _calSelected).OrderBy(ev => ev.When).ToList();

        if (dayEvents.Count == 0)
        {
            EventItems.Children.Add(new TextBlock
            {
                Text = "Nothing planned. Add an event below and Piztki will remind you.",
                FontSize = 11.5,
                Foreground = subtle,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }

        foreach (var ev in dayEvents)
        {
            var row = new Grid { Height = 22 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var time = new TextBlock { Text = ev.When.ToString("HH:mm"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            time.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

            var title = new TextBlock { Text = ev.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = ev.Title };
            if (ev.When < DateTime.Now) title.Foreground = subtle; // already happened
            Grid.SetColumn(title, 1);

            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = "",
                Width = 18,
                Height = 18,
                FontSize = 8,
                Foreground = subtle,
                ToolTip = "Delete this event",
            };
            Grid.SetColumn(remove, 2);
            var captured = ev;
            remove.Click += (_, _) =>
            {
                _s.Events.Remove(captured);
                _calVersion++;
                RebuildCalendar();
                RebuildEvents();
            };

            row.Children.Add(time);
            row.Children.Add(title);
            row.Children.Add(remove);
            EventItems.Children.Add(row);
        }
    }

    /// <summary>Reads "9", "930", "9:30", "09.30", or "14:05" as a time of day.</summary>
    static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        string digits = text.Trim().Replace(".", ":").Replace(" ", "");
        int hour, minute = 0;

        if (digits.Contains(':'))
        {
            var parts = digits.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute)) return false;
        }
        else if (digits.Length is 3 or 4 && int.TryParse(digits, out int packed))
        {
            hour = packed / 100;
            minute = packed % 100;
        }
        else if (!int.TryParse(digits, out hour))
        {
            return false;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59) return false;
        time = new TimeSpan(hour, minute, 0);
        return true;
    }

    void EventBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddEventFromBoxes();
        else if (e.Key == Key.Escape) Keyboard.ClearFocus();
    }

    void EventAdd_Click(object sender, RoutedEventArgs e) => AddEventFromBoxes();

    void AddEventFromBoxes()
    {
        string title = EventTitleBox.Text.Trim();
        if (title.Length == 0)
        {
            EventTitleBox.Focus();
            return;
        }
        if (!TryParseTime(EventTimeBox.Text, out var time))
        {
            // show what a valid time looks like and put the cursor there
            EventTimeBox.Text = "";
            EventTimeBox.Focus();
            Fx.Pop(EventTimeBox, from: 0.9, ms: 300);
            return;
        }

        AddEvent(title, _calSelected + time);
        EventTitleBox.Clear();
    }

    void AddEvent(string title, DateTime when)
    {
        var now = DateTime.Now;
        _s.Events.Add(new CalEvent
        {
            Title = title,
            When = when,
            // an event entered for a moment already gone shouldn't fire a reminder the second it's saved
            Notified = when <= now,
            NotifiedEarly = when <= now.AddMinutes(10),
        });

        _calVersion++;
        RebuildCalendar();
        RebuildEvents();
        Fx.Appear(EventItems, dx: 0, dy: 8, ms: 260);
        BigMascot.Celebrate();
    }

    /// <summary>Called every second: speaks up when an event is ten minutes away and when it starts.</summary>
    void TickEvents()
    {
        if (_s.Events.Count == 0) return;
        var now = DateTime.Now;

        foreach (var ev in _s.Events.ToList())
        {
            if (!ev.Notified && now >= ev.When)
            {
                ev.Notified = true;
                ev.NotifiedEarly = true;
                // if the PC was asleep or Piztki was closed for hours, an old reminder is just noise
                if (now - ev.When < TimeSpan.FromHours(3)) Remind($"{ev.Title} · now");
            }
            else if (_s.EventEarlyReminder && !ev.NotifiedEarly && ev.When - now <= TimeSpan.FromMinutes(10))
            {
                ev.NotifiedEarly = true;
                int minutes = Math.Max(1, (int)Math.Ceiling((ev.When - now).TotalMinutes));
                Remind($"{ev.Title} · in {minutes} min");
            }

            // tidy up: nobody needs last month's reminders
            if (now - ev.When > TimeSpan.FromDays(30)) _s.Events.Remove(ev);
        }
    }

    void Remind(string text)
    {
        Peek(text, celebrate: false);
        _peekUntil = DateTime.Now.AddSeconds(12); // longer than other messages: this one matters
        if (_expanded && ReferenceEquals(_currentPanel, PanelCal)) RebuildEvents();
    }

    void RebuildCalendar()
    {
        var today = DateTime.Now.Date;
        string key = $"{_calMonth:yyyy-MM}|{today:yyyy-MM-dd}|{_s.WeekStartsMonday}|{_calSelected:yyyy-MM-dd}|{_calVersion}";
        if (key == _calBuiltFor) return; // nothing that affects the grid has changed
        _calBuiltFor = key;

        CalTitle.Text = _calMonth.ToString("MMMM yyyy", En);
        CalGrid.Children.Clear();

        var subtle = (Brush)FindResource("SubtleBrush");
        int firstDay = _s.WeekStartsMonday ? 1 : 0;

        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)((firstDay + i) % 7);
            CalGrid.Children.Add(new TextBlock
            {
                Text = day.ToString()[..1],
                FontSize = 10,
                Foreground = subtle,
                TextAlignment = TextAlignment.Center,
                Height = 15,
            });
        }

        int blanks = ((int)_calMonth.DayOfWeek - firstDay + 7) % 7;
        for (int i = 0; i < blanks; i++) CalGrid.Children.Add(new Border { Height = 17 });

        var eventDays = _s.Events
            .Where(ev => ev.When.Year == _calMonth.Year && ev.When.Month == _calMonth.Month)
            .Select(ev => ev.When.Day)
            .ToHashSet();

        int days = DateTime.DaysInMonth(_calMonth.Year, _calMonth.Month);
        for (int d = 1; d <= days; d++)
        {
            var date = new DateTime(_calMonth.Year, _calMonth.Month, d);
            bool isToday = date == today;
            bool isSelected = date == _calSelected;

            var number = new TextBlock
            {
                Text = d.ToString(),
                FontSize = 10.5,
                FontWeight = isToday || eventDays.Contains(d) ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // today is a filled circle, the selected day a ring, a day with events gets a dot under it
            var cell = new Border
            {
                Height = 17,
                Width = 19,
                CornerRadius = new CornerRadius(8.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Brushes.Transparent, // so the whole cell is clickable, not just the digits
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(isSelected && !isToday ? 1 : 0),
            };
            cell.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");

            var content = new Grid();
            content.Children.Add(number);
            if (eventDays.Contains(d))
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 3, Height = 3, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, -1) };
                if (isToday) dot.Fill = (Brush)FindResource("OnAccentBrush");
                else dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
                content.Children.Add(dot);
            }
            cell.Child = content;

            if (isToday)
            {
                cell.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                number.Foreground = (Brush)FindResource("OnAccentBrush");
            }

            cell.MouseLeftButtonUp += (_, _) => SelectDay(date);
            CalGrid.Children.Add(cell);
        }
    }

    void UpdateCalendarSide()
    {
        City1Name.Text = _s.WorldCity1;
        City1Time.Text = Almanac.TimeIn(_s.WorldCity1, _s.TimeFormat);
        City2Name.Text = _s.WorldCity2;
        City2Time.Text = Almanac.TimeIn(_s.WorldCity2, _s.TimeFormat);

        var (phase, lit) = Almanac.Moon(DateTime.UtcNow);
        MoonText.Text = $"{phase} · {lit}%";

        if (Almanac.TryParseDate(_s.CountdownDate, out var target))
        {
            CountdownRow.Visibility = Visibility.Visible;
            CountdownLabel.Text = _s.CountdownName.Trim().Length > 0 ? _s.CountdownName.Trim() : target.ToString("d MMM yyyy", En);
            CountdownText.Text = Almanac.DaysUntil(target);
        }
        else
        {
            CountdownRow.Visibility = Visibility.Collapsed;
        }
    }

    // =====================================================================
    // Clipboard list: how many to keep, and whether it survives a restart
    // =====================================================================

    int ClipMax => _s.ClipMax;

    void LoadSavedClips()
    {
        if (_s.ClipRemember) _clips.AddRange(_s.SavedClips.Take(ClipMax));
    }

    /// <summary>Writes the list to settings when "remember" is on, and wipes the saved copy when it is off.</summary>
    void SaveClips()
    {
        if (!_s.ClipRemember && _s.SavedClips.Count == 0) return;
        _s.SavedClips = _s.ClipRemember ? new List<string>(_clips) : new List<string>();
        _s.Save();
    }

    void TrimClips()
    {
        while (_clips.Count > ClipMax) _clips.RemoveAt(_clips.Count - 1);
        RebuildClip();
        SaveClips();
    }
}
