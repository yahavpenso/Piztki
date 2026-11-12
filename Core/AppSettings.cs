using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piztki.Core;

public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public class TodoItem : ObservableObject
{
    string _text = "";
    bool _done;
    bool _rewarded;

    public string Text { get => _text; set => Set(ref _text, value); }
    public bool Done { get => _done; set => Set(ref _done, value); }
    /// <summary>XP was already given for this task, so re-checking it doesn't pay twice.</summary>
    public bool Rewarded { get => _rewarded; set => Set(ref _rewarded, value); }
}

/// <summary>Something the user put in the calendar, with the moment Piztki should speak up about it.</summary>
public class CalEvent : ObservableObject
{
    string _title = "";
    DateTime _when;
    bool _notified;
    bool _notifiedEarly;

    public string Title { get => _title; set => Set(ref _title, value ?? ""); }
    public DateTime When { get => _when; set => Set(ref _when, value); }
    /// <summary>The "it's time" reminder has been shown.</summary>
    public bool Notified { get => _notified; set => Set(ref _notified, value); }
    /// <summary>The "in 10 minutes" reminder has been shown.</summary>
    public bool NotifiedEarly { get => _notifiedEarly; set => Set(ref _notifiedEarly, value); }
}

public class AppSettings : ObservableObject
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piztki");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    bool _loaded;

    bool _startWithWindows;
    bool _hideInFullscreen = true;
    bool _sounds = true;
    bool _showMascot = true;
    bool _eyesFollowCursor = true;
    string _accent = "#AFA9EC";
    bool _showMusic = true;
    int _focusMinutes = 25;
    int _breakMinutes = 5;
    bool _waterEnabled = true;
    int _waterIntervalMinutes = 60;
    int _waterGoal = 8;
    int _waterCount;
    string _waterDate = "";
    bool _showFocus = true;
    bool _showTodo = true;
    bool _showWater = true;
    string _notchSize = "Medium";
    bool _clickToOpen;
    bool _use24Hour = true;
    bool _danceToMusic = true;
    bool _autoStartBreak = true;
    bool _waveOnOpen = true;
    bool _showShelf = true;
    bool _showClipboard = true;
    bool _showSystem = true;
    bool _showLyrics = true;
    bool _songColors = true;
    bool _volumeHud = true;
    bool _batteryAlerts = true;
    bool _showWeather;
    string _weatherCity = "";
    string _weatherPlace = "";
    double _weatherLat, _weatherLon;
    bool _fahrenheit;
    bool _mascotMoods = true;
    string _texture = "Stars";
    string _hat = "None";
    bool _showLevel = true;
    int _xp;

    public bool ShowShelf { get => _showShelf; set => Set(ref _showShelf, value); }
    public bool ShowClipboard { get => _showClipboard; set => Set(ref _showClipboard, value); }
    public bool ShowSystem { get => _showSystem; set => Set(ref _showSystem, value); }
    public bool ShowLyrics { get => _showLyrics; set => Set(ref _showLyrics, value); }
    /// <summary>Tint the notch with the main color of the playing song's cover.</summary>
    public bool SongColors { get => _songColors; set => Set(ref _songColors, value); }
    public bool VolumeHud { get => _volumeHud; set => Set(ref _volumeHud, value); }
    public bool BatteryAlerts { get => _batteryAlerts; set => Set(ref _batteryAlerts, value); }
    public bool ShowWeather { get => _showWeather; set => Set(ref _showWeather, value); }
    public string WeatherCity { get => _weatherCity; set => Set(ref _weatherCity, value ?? ""); }
    /// <summary>The city the saved coordinates belong to, so we only look a city up once.</summary>
    public string WeatherPlace { get => _weatherPlace; set => Set(ref _weatherPlace, value ?? ""); }
    public double WeatherLat { get => _weatherLat; set => Set(ref _weatherLat, value); }
    public double WeatherLon { get => _weatherLon; set => Set(ref _weatherLon, value); }
    public bool Fahrenheit { get => _fahrenheit; set => Set(ref _fahrenheit, value); }
    public bool MascotMoods { get => _mascotMoods; set => Set(ref _mascotMoods, value); }
    public string Texture { get => _texture; set => Set(ref _texture, value ?? "Stars"); }
    public string Hat { get => _hat; set => Set(ref _hat, value ?? "None"); }
    public bool ShowLevel { get => _showLevel; set => Set(ref _showLevel, value); }
    public int Xp { get => _xp; set => Set(ref _xp, Math.Max(0, value)); }

    // ----- added with the display / style / popups / calendar batch -----

    string _screenName = "";
    bool _notchAttached = true;
    bool _minimalNotch;
    bool _fastAnimations;
    bool _hudCapsLock = true;
    bool _hudLanguage = true;
    bool _hudNetwork = true;
    bool _shelfClearOnStart;
    int _clipMax = 8;
    bool _clipRemember;
    bool _showCalendar = true;
    bool _weekStartsMonday;
    string _worldCity1 = "Tokyo";
    string _worldCity2 = "London";
    string _countdownName = "";
    string _countdownDate = "";
    string _focusDate = "";
    int _focusSecondsToday;

    /// <summary>Windows' name for the monitor the notch lives on; empty means the main one.</summary>
    public string ScreenName { get => _screenName; set => Set(ref _screenName, value ?? ""); }
    /// <summary>True joins the notch to the top edge of the screen; false floats it just below.</summary>
    public bool NotchAttached { get => _notchAttached; set => Set(ref _notchAttached, value); }
    public bool MinimalNotch { get => _minimalNotch; set => Set(ref _minimalNotch, value); }
    public bool FastAnimations { get => _fastAnimations; set => Set(ref _fastAnimations, value); }
    public bool HudCapsLock { get => _hudCapsLock; set => Set(ref _hudCapsLock, value); }
    public bool HudLanguage { get => _hudLanguage; set => Set(ref _hudLanguage, value); }
    public bool HudNetwork { get => _hudNetwork; set => Set(ref _hudNetwork, value); }
    public bool ShelfClearOnStart { get => _shelfClearOnStart; set => Set(ref _shelfClearOnStart, value); }
    public int ClipMax { get => _clipMax; set => Set(ref _clipMax, Math.Clamp(value, 3, 20)); }
    public bool ClipRemember { get => _clipRemember; set => Set(ref _clipRemember, value); }
    public bool ShowCalendar { get => _showCalendar; set => Set(ref _showCalendar, value); }
    public bool WeekStartsMonday { get => _weekStartsMonday; set => Set(ref _weekStartsMonday, value); }
    public string WorldCity1 { get => _worldCity1; set => Set(ref _worldCity1, value ?? ""); }
    public string WorldCity2 { get => _worldCity2; set => Set(ref _worldCity2, value ?? ""); }
    public string CountdownName { get => _countdownName; set => Set(ref _countdownName, value ?? ""); }
    /// <summary>The day being counted down to, as yyyy-MM-dd; empty when no countdown is set.</summary>
    public string CountdownDate { get => _countdownDate; set => Set(ref _countdownDate, value ?? ""); }
    public string FocusDate { get => _focusDate; set => Set(ref _focusDate, value ?? ""); }
    public int FocusSecondsToday { get => _focusSecondsToday; set => Set(ref _focusSecondsToday, Math.Max(0, value)); }

    bool _lyricsPage = true;
    bool _karaokeFill = true;
    bool _lyricsClouds = true;
    bool _musicOpensOnLyrics;

    /// <summary>Offer the full sing-along lyrics page inside the Music tab.</summary>
    public bool LyricsPage { get => _lyricsPage; set => Set(ref _lyricsPage, value); }
    /// <summary>Fill the current line with color as it is sung.</summary>
    public bool KaraokeFill { get => _karaokeFill; set => Set(ref _karaokeFill, value); }
    /// <summary>Drift soft clouds of the cover's colors behind the lyrics.</summary>
    public bool LyricsClouds { get => _lyricsClouds; set => Set(ref _lyricsClouds, value); }
    /// <summary>Remembers that the user last left the Music tab on the lyrics page.</summary>
    public bool MusicOpensOnLyrics { get => _musicOpensOnLyrics; set => Set(ref _musicOpensOnLyrics, value); }

    int _lyricsOffsetMs;
    /// <summary>Shifts the lyrics earlier (negative) or later (positive), for players that report their position a little off.</summary>
    public int LyricsOffsetMs { get => _lyricsOffsetMs; set => Set(ref _lyricsOffsetMs, Math.Clamp(value, -3000, 3000)); }

    bool _eventEarlyReminder = true;
    /// <summary>Also remind ten minutes before an event, not only when it starts.</summary>
    public bool EventEarlyReminder { get => _eventEarlyReminder; set => Set(ref _eventEarlyReminder, value); }

    // ----- notes, sound, shortcuts, downloads, privacy dots, focus history -----

    bool _showNotes = true;
    bool _showSound = true;
    bool _showShortcuts = true;
    bool _showDownloads = true;
    bool _privacyDots = true;
    int _noteIndex;

    public bool ShowNotes { get => _showNotes; set => Set(ref _showNotes, value); }
    public bool ShowSound { get => _showSound; set => Set(ref _showSound, value); }
    public bool ShowShortcuts { get => _showShortcuts; set => Set(ref _showShortcuts, value); }
    /// <summary>Show files being downloaded by a browser (watches the Downloads folder).</summary>
    public bool ShowDownloads { get => _showDownloads; set => Set(ref _showDownloads, value); }
    /// <summary>A small dot in the pill while an app is using the microphone or the camera.</summary>
    public bool PrivacyDots { get => _privacyDots; set => Set(ref _privacyDots, value); }
    /// <summary>Which note the Notes tab was last showing.</summary>
    public int NoteIndex { get => _noteIndex; set => Set(ref _noteIndex, Math.Max(0, value)); }

    // ----- search, notifications, stretch reminders, game mode, themes, picked colors -----

    bool _showSearch = true;
    bool _showNotifications = true;
    bool _stretchReminders = true;
    int _stretchMinutes = 60;
    bool _gameMode;
    string _theme = "Midnight";
    bool _matchSummary = true;
    long _lastMatchWhen;
    string _lastMatchFile = "", _lastMatchHead = "", _lastMatchDetail = "", _lastMatchGame = "Fortnite", _lastMatchIcon = "", _lastMatchMore = "";

    /// <summary>The search / calculator box at the top of the Home tab.</summary>
    public bool ShowSearch { get => _showSearch; set => Set(ref _showSearch, value); }
    /// <summary>Show other apps' Windows notifications in the pill for a moment.</summary>
    public bool ShowNotifications { get => _showNotifications; set => Set(ref _showNotifications, value); }
    /// <summary>Remind the user to stand up after this long at the PC without a break.</summary>
    public bool StretchReminders { get => _stretchReminders; set => Set(ref _stretchReminders, value); }
    public int StretchMinutes { get => _stretchMinutes; set => Set(ref _stretchMinutes, Math.Clamp(value, 15, 180)); }
    /// <summary>A small time / CPU / GPU strip at the top middle while a game fills the screen.</summary>
    public bool GameMode { get => _gameMode; set => Set(ref _gameMode, value); }
    /// <summary>After a Fortnite match, show how it went (read from the game's own replay file).</summary>
    public bool MatchSummary { get => _matchSummary; set => Set(ref _matchSummary, value); }
    /// <summary>The last replay looked at, and what the Home card says about it.</summary>
    public string LastMatchFile { get => _lastMatchFile; set => Set(ref _lastMatchFile, value ?? ""); }
    /// <summary>Which game the last match was in: the card names it, and another game's match replaces it.</summary>
    public string LastMatchGame { get => _lastMatchGame; set => Set(ref _lastMatchGame, string.IsNullOrWhiteSpace(value) ? "Fortnite" : value); }
    /// <summary>When that match ended (ticks, local time): only something newer replaces the card.</summary>
    public long LastMatchWhen { get => _lastMatchWhen; set => Set(ref _lastMatchWhen, value); }
    /// <summary>A picture of the game for the card (a file on this PC), and the lines a click on the card shows.</summary>
    public string LastMatchIcon { get => _lastMatchIcon; set => Set(ref _lastMatchIcon, value ?? ""); }
    public string LastMatchMore { get => _lastMatchMore; set => Set(ref _lastMatchMore, value ?? ""); }
    public string LastMatchHead { get => _lastMatchHead; set => Set(ref _lastMatchHead, value ?? ""); }
    public string LastMatchDetail { get => _lastMatchDetail; set => Set(ref _lastMatchDetail, value ?? ""); }
    /// <summary>The notch's look: Midnight, Neon, Glass, or Pastel.</summary>
    public string Theme { get => _theme; set => Set(ref _theme, value ?? "Midnight"); }

    // ----- Discord -----

    string _discordAppId = "", _discordSecret = "", _discordAccess = "", _discordRefresh = "";
    bool _discordPresence = true;
    bool _discordVoice = true;

    /// <summary>The user's own Discord application (from the Discord developer page).</summary>
    public string DiscordAppId { get => _discordAppId; set => Set(ref _discordAppId, (value ?? "").Trim()); }
    /// <summary>That application's secret; only needed for voice controls. Kept on this PC only.</summary>
    public string DiscordSecret { get => _discordSecret; set => Set(ref _discordSecret, (value ?? "").Trim()); }
    /// <summary>Sign-in tokens Discord handed out for voice controls.</summary>
    public string DiscordAccess { get => _discordAccess; set => Set(ref _discordAccess, value ?? ""); }
    public string DiscordRefresh { get => _discordRefresh; set => Set(ref _discordRefresh, value ?? ""); }
    /// <summary>Show the running timer or the playing song on the Discord profile.</summary>
    public bool DiscordPresence { get => _discordPresence; set => Set(ref _discordPresence, value); }
    /// <summary>Mute, deafen, and who is talking, in the Sound tab.</summary>
    public bool DiscordVoice { get => _discordVoice; set => Set(ref _discordVoice, value); }

    /// <summary>Colors picked from the screen, newest first, as #RRGGBB.</summary>
    public List<string> RecentColors { get; set; } = new();

    /// <summary>Quick notes. The Notes tab edits these in place and saves a moment after typing stops.</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>Apps, files, and folders pinned to the Shortcuts tab (full paths).</summary>
    public ObservableCollection<string> Shortcuts { get; set; } = new();

    /// <summary>Seconds of focus per day (yyyy-MM-dd), for the weekly chart. Old days are dropped.</summary>
    public Dictionary<string, int> FocusHistory { get; set; } = new();

    /// <summary>The user's own calendar events (kept on this PC only).</summary>
    public ObservableCollection<CalEvent> Events { get; set; } = new();

    /// <summary>Clipboard entries kept across restarts, only while ClipRemember is on.</summary>
    public List<string> SavedClips { get; set; } = new();

    /// <summary>Files parked on the shelf (full paths).</summary>
    public ObservableCollection<string> Shelf { get; set; } = new();

    // Levels get steadily further apart: 50 XP to reach level 2, 100 more for 3, 150 more for 4...
    public static int LevelFor(int xp) => 1 + (int)Math.Floor((Math.Sqrt(1 + 8 * xp / 50.0) - 1) / 2);
    static int XpAtLevel(int level) => 50 * (level - 1) * level / 2;

    [JsonIgnore]
    public int Level => LevelFor(_xp);

    [JsonIgnore]
    public double LevelProgress
    {
        get
        {
            int level = Level, start = XpAtLevel(level), next = XpAtLevel(level + 1);
            return Math.Clamp((_xp - start) / (double)(next - start), 0, 1);
        }
    }

    public bool ShowFocus { get => _showFocus; set => Set(ref _showFocus, value); }
    public bool ShowTodo { get => _showTodo; set => Set(ref _showTodo, value); }
    public bool ShowWater { get => _showWater; set => Set(ref _showWater, value); }
    public string NotchSize { get => _notchSize; set => Set(ref _notchSize, value); }
    public bool ClickToOpen { get => _clickToOpen; set => Set(ref _clickToOpen, value); }
    public bool Use24Hour { get => _use24Hour; set => Set(ref _use24Hour, value); }
    public bool DanceToMusic { get => _danceToMusic; set => Set(ref _danceToMusic, value); }
    public bool AutoStartBreak { get => _autoStartBreak; set => Set(ref _autoStartBreak, value); }
    public bool WaveOnOpen { get => _waveOnOpen; set => Set(ref _waveOnOpen, value); }

    [JsonIgnore]
    public double NotchScale => _notchSize switch { "Small" => 0.88, "Large" => 1.15, _ => 1.0 };

    [JsonIgnore]
    public string TimeFormat => _use24Hour ? "HH:mm" : "h:mm tt";

    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value); }
    public bool Sounds { get => _sounds; set => Set(ref _sounds, value); }
    public bool ShowMascot { get => _showMascot; set => Set(ref _showMascot, value); }
    public bool EyesFollowCursor { get => _eyesFollowCursor; set => Set(ref _eyesFollowCursor, value); }
    public string Accent { get => _accent; set => Set(ref _accent, value); }
    public bool ShowMusic { get => _showMusic; set => Set(ref _showMusic, value); }
    public int FocusMinutes { get => _focusMinutes; set => Set(ref _focusMinutes, value); }
    public int BreakMinutes { get => _breakMinutes; set => Set(ref _breakMinutes, value); }
    public bool WaterEnabled { get => _waterEnabled; set => Set(ref _waterEnabled, value); }
    public int WaterIntervalMinutes { get => _waterIntervalMinutes; set => Set(ref _waterIntervalMinutes, value); }
    public int WaterGoal { get => _waterGoal; set => Set(ref _waterGoal, value); }
    public int WaterCount { get => _waterCount; set => Set(ref _waterCount, value); }
    public string WaterDate { get => _waterDate; set => Set(ref _waterDate, value); }

    public ObservableCollection<TodoItem> Todos { get; set; } = new();

    [JsonIgnore]
    public static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            s = new AppSettings();
        }

        s.Clamp();
        s.Hook();
        s._loaded = true;
        return s;
    }

    void Clamp()
    {
        _focusMinutes = Math.Clamp(_focusMinutes, 5, 90);
        _breakMinutes = Math.Clamp(_breakMinutes, 1, 30);
        _waterIntervalMinutes = Math.Clamp(_waterIntervalMinutes, 15, 240);
        _waterGoal = Math.Clamp(_waterGoal, 4, 12);
        _waterCount = Math.Max(0, _waterCount);
        Todos ??= new();
        Shelf ??= new();
        SavedClips ??= new();
        Events ??= new();
        Notes ??= new();
        Shortcuts ??= new();
        FocusHistory ??= new();
        RecentColors ??= new();
        _stretchMinutes = Math.Clamp(_stretchMinutes, 15, 180);
        _clipMax = Math.Clamp(_clipMax, 3, 20);
    }

    void Hook()
    {
        PropertyChanged += (_, _) => Save();
        Shelf.CollectionChanged += (_, _) => Save();
        Shortcuts.CollectionChanged += (_, _) => Save();
        foreach (var ev in Events) ev.PropertyChanged += OnTodoChanged; // same job: save on any change
        Events.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (CalEvent ev in e.NewItems) ev.PropertyChanged += OnTodoChanged;
            if (e.OldItems != null)
                foreach (CalEvent ev in e.OldItems) ev.PropertyChanged -= OnTodoChanged;
            Save();
        };
        foreach (var t in Todos) t.PropertyChanged += OnTodoChanged;
        Todos.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (TodoItem t in e.NewItems) t.PropertyChanged += OnTodoChanged;
            if (e.OldItems != null)
                foreach (TodoItem t in e.OldItems) t.PropertyChanged -= OnTodoChanged;
            if (e.Action != NotifyCollectionChangedAction.Move) Save();
        };
    }

    void OnTodoChanged(object? sender, PropertyChangedEventArgs e) => Save();

    public void Save()
    {
        if (!_loaded) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // Settings are best-effort; a failed write must never crash the notch.
        }
    }
}
