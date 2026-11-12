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
using System.Windows.Threading;
using Piztki.Core;

namespace Piztki;

// Notes, shortcuts, the per-app volume mixer, the microphone / camera dots, downloads,
// screenshots, and the weekly focus chart.
public partial class NotchWindow
{
    void InitTools()
    {
        _noteSave.Tick += (_, _) =>
        {
            _noteSave.Stop();
            _s.Save();
        };
        if (_s.Notes.Count == 0) _s.Notes.Add("");
        ShowNote(Math.Min(_s.NoteIndex, _s.Notes.Count - 1));

        RebuildShortcuts();

        _downloads.Started += () =>
        {
            Peek("Download started", celebrate: false, quiet: true);
        };
        _downloads.Finished += path =>
        {
            Peek("Downloaded " + Shorten(Path.GetFileName(path), 34), celebrate: false);
            if (DownloadCardWanted != _downloadCardShown) RebuildHome();
        };
        ApplyDownloadWatch();
    }

    /// <summary>Once a second, from the main tick.</summary>
    void TickTools()
    {
        _downloads.Poll();
        if (DownloadCardWanted != _downloadCardShown) RebuildHome();

        if (!_selfTest && ++_privacyCount % 2 == 0) PollPrivacy(announce: true);
    }

    static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // =====================================================================
    // Notes: a few plain notes, saved a moment after typing stops
    // =====================================================================

    const int NotesMax = 20;

    readonly DispatcherTimer _noteSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool _noteLoading;

    void ShowNote(int index, int slide = 0)
    {
        index = Math.Clamp(index, 0, _s.Notes.Count - 1);
        _s.NoteIndex = index;

        _noteLoading = true;
        NoteBox.Text = _s.Notes[index];
        _noteLoading = false;

        NoteTitle.Text = $"Note {index + 1} of {_s.Notes.Count}";
        UpdateNoteLook();
        if (slide != 0) Fx.Appear(NoteBox, dx: slide * 16, dy: 0, ms: 260);
    }

    void UpdateNoteLook()
    {
        NoteHint.Visibility = NoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // a note that starts in Hebrew or Arabic reads from the right
        NoteBox.FlowDirection = StartsRightToLeft(NoteBox.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    static bool StartsRightToLeft(string text)
    {
        foreach (char c in text)
        {
            if (c >= 0x0590 && c <= 0x08FF) return true;
            if (char.IsLetter(c)) return false;
        }
        return false;
    }

    void NoteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_noteLoading || !_ready) return;
        _s.Notes[_s.NoteIndex] = NoteBox.Text;
        UpdateNoteLook();
        _noteSave.Stop();
        _noteSave.Start();
    }

    void NoteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    void NotePrev_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Notes.Count > 1) ShowNote((_s.NoteIndex + _s.Notes.Count - 1) % _s.Notes.Count, slide: -1);
    }

    void NoteNext_Click(object sender, RoutedEventArgs e)
    {
        if (_s.Notes.Count > 1) ShowNote((_s.NoteIndex + 1) % _s.Notes.Count, slide: 1);
    }

    void NoteNew_Click(object sender, RoutedEventArgs e) => NewNote();

    void NewNote()
    {
        // an empty note is already "a new note": don't pile up blanks
        int blank = _s.Notes.FindIndex(string.IsNullOrWhiteSpace);
        if (blank >= 0)
        {
            if (blank != _s.NoteIndex) ShowNote(blank, slide: 1);
        }
        else if (_s.Notes.Count >= NotesMax)
        {
            Peek($"{NotesMax} notes is the limit", celebrate: false, quiet: true);
            return;
        }
        else
        {
            _s.Notes.Add("");
            ShowNote(_s.Notes.Count - 1, slide: 1);
            _s.Save();
        }
        NoteBox.Focus();
    }

    void NoteDelete_Click(object sender, RoutedEventArgs e) => DeleteNote();

    void DeleteNote()
    {
        if (_s.Notes.Count <= 1) _s.Notes[0] = "";
        else _s.Notes.RemoveAt(_s.NoteIndex);
        ShowNote(Math.Min(_s.NoteIndex, _s.Notes.Count - 1));
        Fx.Appear(NoteBox, dy: 8, ms: 260);
        _s.Save();
    }

    // =====================================================================
    // Shortcuts: pinned apps, files, and folders
    // =====================================================================

    const int ShortcutMax = 21;

    void ShortcutAddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick an app or a file to pin",
            Filter = "Everything|*.*",
            Multiselect = true,
            DereferenceLinks = false, // keep Start-menu shortcuts as they are, with their own icon and name
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        if (dialog.ShowDialog() == true) AddShortcuts(dialog.FileNames);
    }

    void ShortcutAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a folder to pin", Multiselect = true };
        if (dialog.ShowDialog() == true) AddShortcuts(dialog.FolderNames);
    }

    void AddShortcuts(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            if (_s.Shortcuts.Count >= ShortcutMax) break;
            if (_s.Shortcuts.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            _s.Shortcuts.Add(path);
            added++;
        }
        if (added == 0) return;
        RebuildShortcuts(popLast: added);
        BigMascot.Celebrate();
    }

    void RebuildShortcuts(int popLast = 0)
    {
        ShortcutItems.Children.Clear();
        for (int i = 0; i < _s.Shortcuts.Count; i++)
        {
            var tile = CreateShortcutTile(_s.Shortcuts[i]);
            ShortcutItems.Children.Add(tile);
            if (i >= _s.Shortcuts.Count - popLast) Fx.Pop(tile, from: 0.5, ms: 420, delay: (i - (_s.Shortcuts.Count - popLast)) * 60);
        }
        ShortcutEmpty.Visibility = _s.Shortcuts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string ShortcutName(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        string name = Directory.Exists(path) ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed);
        return name.Length == 0 ? path : name;
    }

    FrameworkElement CreateShortcutTile(string path)
    {
        bool exists = File.Exists(path) || Directory.Exists(path);
        string name = ShortcutName(path);

        var body = new StackPanel();
        var icon = exists ? LargeIcon(path) : null;
        if (icon != null)
        {
            body.Children.Add(new Image { Source = icon, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center });
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = "", // warning sign: the target is gone
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 22,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("SubtleBrush"),
            });
        }
        body.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 10.5,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var tile = new Button
        {
            Style = (Style)FindResource("RowButton"),
            Width = 70,
            Content = body,
            Opacity = exists ? 1 : 0.5,
            ToolTip = (exists ? name : "This was moved or deleted") + "\nRight-click to unpin",
        };
        tile.Click += (_, _) =>
        {
            if (!exists) return;
            Fx.Pop(tile, from: 0.88, ms: 320);
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        };
        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            tile.IsHitTestVisible = false;
            Fx.Disappear(tile, dy: -6, ms: 150, done: () =>
            {
                _s.Shortcuts.Remove(path);
                RebuildShortcuts();
            });
        };
        return tile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

    readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The 32-pixel icon Explorer shows for a file, a shortcut, or a folder.</summary>
    ImageSource? LargeIcon(string path)
    {
        if (_iconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? result = null;
        try
        {
            var info = new SHFILEINFO();
            const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (info.hIcon != IntPtr.Zero)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                DestroyIcon(info.hIcon);
                result = source;
            }
        }
        catch
        {
            // no icon: the caller draws a glyph instead
        }
        _iconCache[path] = result;
        return result;
    }

    // =====================================================================
    // Sound: one volume slider per app, and the microphone
    // =====================================================================

    sealed class SoundRow
    {
        public MixerApp App = null!;
        public Slider Slider = null!;
        public TextBlock Percent = null!;
        public Button Mute = null!;
    }

    readonly List<SoundRow> _soundRows = new();
    string _soundKey = "";
    bool _soundSyncing;

    void UpdateSound(bool rebuild)
    {
        UpdateMicLine();

        // don't pull the slider out from under the pointer while it is being dragged
        if (!rebuild && Mouse.LeftButton == MouseButtonState.Pressed && PanelSound.IsMouseOver) return;

        var apps = Mixer.ReadApps();
        string key = string.Join(",", apps.Select(a => a.ProcessId));
        if (key != _soundKey || rebuild)
        {
            _soundKey = key;
            BuildSoundRows(apps, animate: rebuild);
            return;
        }

        // same apps as a second ago: just follow volume changes made elsewhere (the app itself, Windows' mixer)
        _soundSyncing = true;
        for (int i = 0; i < apps.Count && i < _soundRows.Count; i++)
        {
            var row = _soundRows[i];
            row.App = apps[i];
            if (Math.Abs(row.Slider.Value - apps[i].Volume) > 0.005) row.Slider.Value = apps[i].Volume;
            ShowSoundLevel(row);
        }
        _soundSyncing = false;
    }

    void BuildSoundRows(List<MixerApp> apps, bool animate)
    {
        SoundItems.Children.Clear();
        _soundRows.Clear();
        SoundEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var iconFont = (FontFamily)FindResource("Icons");
        int index = 0;
        foreach (var app in apps)
        {
            var grid = new Grid { Height = 27 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

            var icon = app.ExePath != null ? LargeIcon(app.ExePath) : null;
            if (icon != null)
            {
                grid.Children.Add(new Image { Source = icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
            }
            else
            {
                grid.Children.Add(new TextBlock
                {
                    Text = app.ProcessId == 0 ? "" : "", // a bell for system sounds, a speaker otherwise
                    FontFamily = iconFont,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("SubtleBrush"),
                });
            }

            var name = new TextBlock { Text = app.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = app.Name };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var slider = new Slider { Style = (Style)FindResource("DarkSlider"), Minimum = 0, Maximum = 1, Value = app.Volume, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
            Grid.SetColumn(slider, 2);
            grid.Children.Add(slider);

            var percent = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(percent, 3);
            grid.Children.Add(percent);

            var mute = new Button { Style = (Style)FindResource("IconButton"), Width = 22, Height = 22, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(mute, 4);
            grid.Children.Add(mute);

            var row = new SoundRow { App = app, Slider = slider, Percent = percent, Mute = mute };
            _soundRows.Add(row);
            ShowSoundLevel(row);

            slider.ValueChanged += (_, e) =>
            {
                if (_soundSyncing) return;
                row.App.SetVolume(e.NewValue);
                ShowSoundLevel(row);
            };
            mute.Click += (_, _) =>
            {
                row.App.SetMuted(!row.App.Muted);
                ShowSoundLevel(row);
                Fx.Pop(mute, from: 0.7, ms: 300);
            };

            SoundItems.Children.Add(grid);
            if (animate) Fx.Appear(grid, dx: 0, dy: 8, ms: 260, delay: index * 40);
            index++;
        }
    }

    static void ShowSoundLevel(SoundRow row)
    {
        bool silent = row.App.Muted || row.App.Volume < 0.005;
        row.Percent.Text = row.App.Muted ? "Muted" : $"{Math.Round(row.App.Volume * 100)}%";
        row.Percent.Opacity = row.App.Muted ? 0.55 : 1;
        row.Slider.Opacity = row.App.Muted ? 0.4 : 1;
        row.Mute.Content = silent ? "" : "";
        row.Mute.ToolTip = row.App.Muted ? "Unmute " + row.App.Name : "Mute " + row.App.Name;
    }

    static readonly Brush MicIdleBrush = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    static readonly Brush MicLiveBrush = Frozen(Color.FromRgb(0xFF, 0xA5, 0x3D));
    static readonly Brush MicMutedBrush = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    bool _micPresent, _micMuted;

    void UpdateMicLine()
    {
        _micPresent = Mixer.TryGetMicMuted(out _micMuted);

        string text = !_micPresent ? "No microphone found"
            : _micMuted ? "Microphone is muted"
            : _micUser != null ? "Microphone in use · " + _micUser
            : "Microphone is on · not in use";
        if (_camUser != null) text += "   Camera · " + _camUser;

        MicStateText.Text = text;
        MicStateDot.Fill = _micMuted ? MicMutedBrush : _micUser != null || _camUser != null ? MicLiveBrush : MicIdleBrush;
        MicMuteBtn.Visibility = _micPresent ? Visibility.Visible : Visibility.Collapsed;
        MicMuteBtn.Content = _micMuted ? "Unmute mic" : "Mute mic";
    }

    void MicMute_Click(object sender, RoutedEventArgs e)
    {
        if (!Mixer.TryGetMicMuted(out bool muted)) return;
        if (!Mixer.SetMicMuted(!muted)) return;
        UpdateMicLine();
        Fx.Pop(MicMuteBtn, from: 0.85, ms: 300);
        Fx.Pop(MicStateDot, from: 0.3, ms: 380);
    }

    // =====================================================================
    // Microphone / camera dots in the pill
    // =====================================================================

    string? _micUser, _camUser;
    int _privacyCount;

    /// <summary>Extra pill width taken by the dots that are showing.</summary>
    double PrivacyWidth
    {
        get
        {
            double dots = (NetDot.Visibility == Visibility.Visible ? 9 : 0) + (MicDot.Visibility == Visibility.Visible ? 9 : 0)
                          + (CamDot.Visibility == Visibility.Visible ? 8 : 0);
            // the floating pill's outline takes a pixel on each side, which used to cut into the last dot
            return dots > 0 ? dots + 4 : 0;
        }
    }

    void PollPrivacy(bool announce)
    {
        string? mic = null, cam = null;
        if (_s.PrivacyDots && !_selfTest)
        {
            mic = PrivacyWatch.MicrophoneUser();
            cam = PrivacyWatch.CameraUser();
        }
        SetPrivacy(mic, cam, announce);
    }

    void SetPrivacy(string? mic, string? cam, bool announce)
    {
        if (mic == _micUser && cam == _camUser) return;
        bool micStarted = mic != null && _micUser == null;
        bool camStarted = cam != null && _camUser == null;
        _micUser = mic;
        _camUser = cam;

        ShowDot(MicDot, mic != null, mic != null ? mic + " is using the microphone" : null);
        ShowDot(CamDot, cam != null, cam != null ? cam + " is using the camera" : null);

        if (announce && camStarted) Peek("Camera on · " + Shorten(cam!, 24), celebrate: false, quiet: true);
        else if (announce && micStarted) Peek("Microphone on · " + Shorten(mic!, 22), celebrate: false, quiet: true);
        else UpdateMini();

        if (_expanded && ReferenceEquals(_currentPanel, PanelSound)) UpdateMicLine();
    }

    static void ShowDot(FrameworkElement dot, bool on, string? tip)
    {
        dot.ToolTip = tip;
        if (on && dot.Visibility != Visibility.Visible)
        {
            dot.Visibility = Visibility.Visible;
            Fx.Pop(dot, from: 0.2, ms: 420);
        }
        else if (!on)
        {
            dot.Visibility = Visibility.Collapsed;
        }
    }

    // =====================================================================
    // Downloads
    // =====================================================================

    readonly DownloadWatch _downloads = new();
    bool _downloadCardShown;

    void ApplyDownloadWatch()
    {
        if (_s.ShowDownloads && !_selfTest) _downloads.Start();
        else _downloads.Stop();
    }

    /// <summary>What the pill says while a download runs, or null.</summary>
    string? DownloadText()
        => _s.ShowDownloads && _downloads.Count > 0 ? "Downloading · " + DownloadWatch.Size(_downloads.Bytes) : null;

    bool RecentlyDownloaded => _downloads.LastFinished != null && DateTime.Now - _downloads.LastFinishedAt < TimeSpan.FromMinutes(10);

    bool DownloadCardWanted => _s.ShowDownloads && (_downloads.Count > 0 || RecentlyDownloaded);

    void OpenLastDownload()
    {
        try
        {
            if (_downloads.Count == 0 && _downloads.LastFinished is { } file && File.Exists(file))
                Process.Start("explorer.exe", $"/select,\"{file}\"");
            else if (Directory.Exists(_downloads.Folder))
                Process.Start(new ProcessStartInfo(_downloads.Folder) { UseShellExecute = true });
        }
        catch { }
    }

    void UpdateToolCards(Action<string, string, string> set)
    {
        int written = _s.Notes.Count(n => !string.IsNullOrWhiteSpace(n));
        string firstLine = _s.Notes.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim().Split('\n')[0].Trim() ?? "";
        set("notes", written == 0 ? "Empty" : written.ToString(), written == 0 ? "Write a note" : firstLine);

        set("shortcuts", _s.Shortcuts.Count == 0 ? "None" : _s.Shortcuts.Count.ToString(), _s.Shortcuts.Count == 0 ? "Pin an app" : "pinned");

        if (_homeCards.ContainsKey("sound"))
        {
            if (_volume.TryRead(out double level, out bool muted))
                set("sound", muted ? "Muted" : $"{Math.Round(level * 100)}%", _micUser != null ? "Mic in use" : "Volume");
            else
                set("sound", "–", "No speakers");
        }

        if (_downloads.Count > 0)
            set("download", DownloadWatch.Size(_downloads.Bytes), SystemStats.Speed(_downloads.BytesPerSec) + (_downloads.Count > 1 ? $" · {_downloads.Count} files" : ""));
        else if (_downloads.LastFinished is { } file)
            set("download", Path.GetFileName(file), "Downloaded. Click to show it");
    }

    // =====================================================================
    // Screenshot: drag over part of the screen, the picture lands on the shelf
    // =====================================================================

    bool _snipping;

    async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_snipping) return;
        _snipping = true;
        try
        {
            // the notch must not be in its own picture
            Opacity = 0;
            await Task.Delay(140);
            var shot = SnipWindow.CaptureDesktop();
            SetExpanded(false);

            var picker = new SnipWindow(shot);
            picker.ShowDialog();
            Opacity = 1;

            if (picker.Selection is { } area) KeepScreenshot(shot, area, null);
        }
        catch
        {
            Peek("Couldn't take the screenshot", celebrate: false, quiet: true);
        }
        finally
        {
            Opacity = 1;
            _snipping = false;
        }
    }

    /// <summary>Saves the chosen area, copies it, and parks the file on the shelf.</summary>
    string KeepScreenshot(BitmapSource shot, Int32Rect area, string? folder)
    {
        var (path, image) = SnipWindow.SaveCrop(shot, area, folder);
        try { Clipboard.SetImage(image); } catch { /* clipboard busy: the file is saved either way */ }

        bool room = _s.Shelf.Count < ShelfMax;
        if (room) AddToShelf(new[] { path });
        Peek(room ? "Screenshot saved to the shelf" : "Screenshot saved to Pictures", celebrate: false);
        return path;
    }

    // =====================================================================
    // Focus time, day by day
    // =====================================================================

    void RecordFocusDay(int seconds)
    {
        _s.FocusHistory[AppSettings.Today] = seconds;

        // a month is plenty for a seven-day chart
        string cutoff = DateTime.Now.AddDays(-31).ToString("yyyy-MM-dd");
        foreach (var old in _s.FocusHistory.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            _s.FocusHistory.Remove(old);
    }

    int FocusSecondsOn(DateTime day)
    {
        string key = day.ToString("yyyy-MM-dd");
        _s.FocusHistory.TryGetValue(key, out int seconds);
        // today's number also lives in the running counter, which is a little ahead of what was last saved
        if (key == AppSettings.Today && _s.FocusDate == key) seconds = Math.Max(seconds, _s.FocusSecondsToday + _focusUnsaved);
        return seconds;
    }

    static string Duration(int seconds)
    {
        int minutes = seconds / 60;
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    void RebuildStats(bool animate)
    {
        var today = DateTime.Now.Date;
        var days = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).ToArray();
        var seconds = days.Select(FocusSecondsOn).ToArray();
        int total = seconds.Sum();
        // one focus session is the smallest scale, so a single short day doesn't fill the chart
        double top = Math.Max(seconds.Max(), 25 * 60);

        StatsTitle.Text = total >= 60 ? "Focused " + Duration(total) : "No focus time yet";
        int best = Array.IndexOf(seconds, seconds.Max());
        StatsSub.Text = total >= 60 ? "Best day: " + (days[best] == today ? "today" : days[best].ToString("dddd", En)) : "Start a focus session to fill this in";

        var subtle = (Brush)FindResource("SubtleBrush");
        var restBrush = Frozen(Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF));
        StatsBars.Children.Clear();

        for (int i = 0; i < 7; i++)
        {
            bool isToday = days[i] == today;
            int minutes = seconds[i] / 60;

            var column = new Grid { Margin = new Thickness(3, 0, 3, 0) };
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(17) });

            column.Children.Add(new TextBlock
            {
                Text = minutes > 0 ? (minutes >= 60 ? $"{minutes / 60}h{minutes % 60:00}" : minutes + "m") : "",
                FontSize = 10,
                Foreground = subtle,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
            });

            var track = new Border { CornerRadius = new CornerRadius(5), Background = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(6, 2, 6, 2) };
            Grid.SetRow(track, 1);
            column.Children.Add(track);

            var scale = new ScaleTransform(1, 0);
            var bar = new Border
            {
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Stretch,
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = scale,
                Background = restBrush,
            };
            if (isToday) bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            Grid.SetRow(bar, 1);
            column.Children.Add(bar);

            double height = seconds[i] <= 0 ? 0 : Math.Max(0.06, seconds[i] / top);
            if (animate) Fx.Animate(scale, ScaleTransform.ScaleYProperty, height, 520, Fx.EaseOut, delay: 60 + i * 45);
            else scale.ScaleY = height;

            var label = new TextBlock
            {
                Text = isToday ? "Today" : days[i].ToString("ddd", En),
                FontSize = 10.5,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = subtle,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            if (isToday) label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            Grid.SetRow(label, 2);
            column.Children.Add(label);

            StatsBars.Children.Add(column);
        }
    }
}
