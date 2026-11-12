using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Piztki.Core;

namespace Piztki;

/// <summary>The notch's ready-made looks.</summary>
public static class NotchThemes
{
    public static readonly string[] Names = { "Midnight", "Neon", "Glass", "Pastel" };

    /// <summary>Body color, outline color (null = use the glow color), and the glow color that suits it.</summary>
    public static (Color back, Color? border, string accent) Look(string theme) => theme switch
    {
        "Neon" => (Color.FromRgb(0x04, 0x04, 0x07), null, "#5DE8FF"),
        "Glass" => (Color.FromArgb(0xB4, 0x16, 0x18, 0x24), Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF), "#85B7EB"),
        "Pastel" => (Color.FromRgb(0x2C, 0x25, 0x3D), Color.FromArgb(0x55, 0xFF, 0xD6, 0xF0), "#ED93B1"),
        _ => (Color.FromRgb(0x0A, 0x0A, 0x0D), Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), "#AFA9EC"),
    };
}

// The search / calculator box, the pull-down quick timer, the color picker, other apps'
// notifications, stretch reminders, Bluetooth battery levels, themes, and game mode.
public partial class NotchWindow
{
    readonly NotificationWatch _notifications = new();
    readonly GpuLoad _gpu = new();
    DateTime _activeSince = DateTime.Now;
    bool _offline;
    int _extrasCount;

    void InitExtras()
    {
        _notifications.Arrived += OnNotification;
        BuildQuickChips();
        RebuildColorSwatches();
    }

    /// <summary>Once a second, from the main tick.</summary>
    void TickExtras()
    {
        _extrasCount++;
        if (_selfTest) return;

        if (_s.ShowNotifications && _extrasCount % 2 == 0) _notifications.Poll();
        TickStretch();
        if (_s.MatchSummary && _extrasCount % 3 == 0) _fortnite.Poll();
        if (_s.MatchSummary && _extrasCount % 3 == 1) PollRoblox();
        if (_s.MatchSummary && _extrasCount % 2 == 0) _games.Poll(_hwnd);
        // a few seconds after start, then once a minute
        if (_s.BatteryAlerts && _extrasCount % 60 == 5) _ = ReadDeviceBatteries();
    }

    // =====================================================================
    // Games: how the last match went (Fortnite), or what was played (Roblox)
    // =====================================================================

    readonly FortniteWatch _fortnite = new();
    GameHudWindow? _matchStrip;

    void InitFortnite()
    {
        _fortnite.Done = _s.LastMatchFile;
        _fortnite.Finished += OnMatchFinished;
        _fortnite.Skipped += file => _s.LastMatchFile = file;
        _roblox.Finished += visit => Dispatcher.BeginInvoke(() => OnRobloxLeft(visit));
        _roblox.Joined += visit => Dispatcher.BeginInvoke(() => OnRobloxJoined(visit));

        foreach (string name in NotGames) _games.NotGames.Add(name);
        foreach (string name in new[] { "FortniteClient-Win64-Shipping", "RobloxPlayerBeta", "Piztki", "devenv", "Code", "claude" }) _games.HandledElsewhere.Add(name);
        _games.Ended += OnGameEnded;
        _cs2.MatchEnded += match => Dispatcher.BeginInvoke(() => OnCs2Match(match));
        ApplyCs2(announce: false);
    }

    void OnMatchFinished(FortniteMatch match, bool fresh)
    {
        _s.LastMatchFile = match.File;
        var more = new[]
        {
            $"{match.Game} · {match.Headline} · {match.Detail}",
            $"{match.Assists} {(match.Assists == 1 ? "assist" : "assists")} · {match.Revives} {(match.Revives == 1 ? "revive" : "revives")} · {match.DamageTaken} dmg taken · {match.Kilometres:0.0} km · {Math.Round(match.Minutes)} min",
        };
        ShowGameResult(match.Game, match.Headline, match.Detail, more, match.Line, match.Ended, fresh, match.Won);
    }

    // ----- Roblox: which game and server on the way in, how long on the way out -----

    readonly RobloxWatch _roblox = new();
    bool _robloxBusy;

    /// <summary>What was looked up about the game the player is in, kept for the summary at the end.</summary>
    sealed record RobloxNow(long Universe, RobloxGame? Game, RobloxServer? Server, string? Where, string? Icon);
    RobloxNow? _robloxNow;
    bool _robloxPlaying;

    void PollRoblox()
    {
        if (_robloxBusy) return;
        _robloxBusy = true;
        Task.Run(() =>
        {
            _roblox.Poll(); // reads the log off the UI thread; it can be several megabytes at first
            _robloxBusy = false;
        });
    }

    async Task<RobloxNow> LookUpRoblox(RobloxSession visit, bool withServer)
    {
        var game = RobloxInfo.Game(visit.UniverseId);
        var where = RobloxInfo.Where(visit.ServerIp);
        var icon = RobloxInfo.Icon(visit.UniverseId);
        var server = withServer ? RobloxInfo.Server(visit.PlaceId, visit.JobId) : Task.FromResult<RobloxServer?>(null);
        await Task.WhenAll(game, where, icon, server);
        return new RobloxNow(visit.UniverseId, game.Result, server.Result, where.Result, icon.Result);
    }

    /// <summary>"Frankfurt, Germany · 28/35 players · ~56 ms", with whatever of it is known.</summary>
    static string ServerText(RobloxNow now, string gap, bool fps = false)
    {
        var parts = new List<string>();
        if (now.Where != null) parts.Add(now.Where);
        if (now.Server is { } s)
        {
            parts.Add($"{s.Playing}/{s.MaxPlayers} players");
            if (s.Ping > 0) parts.Add($"~{s.Ping} ms");
            if (fps && s.Fps > 0) parts.Add($"server {Math.Round(s.Fps)} fps");
        }
        return string.Join(gap, parts);
    }

    /// <summary>"12.4K playing · by Creator".</summary>
    static string GameText(RobloxGame? game, bool visits = false)
    {
        if (game == null) return "";
        var parts = new List<string>();
        parts.Add(RobloxInfo.Short(game.Playing) + " playing"); // the number first: a long creator name may get cut off
        if (game.Creator.Length > 0) parts.Add("by " + game.Creator);
        if (visits)
        {
            parts.Add(RobloxInfo.Short(game.Visits) + " visits");
            if (game.UpdatedText.Length > 0) parts.Add(game.UpdatedText);
        }
        return string.Join(" · ", parts);
    }

    /// <summary>The player has just landed in a Roblox game: say which game and what the server is like.</summary>
    async void OnRobloxJoined(RobloxSession visit)
    {
        try
        {
            if (!_s.MatchSummary) return;
            DateTime joined = visit.Joined.ToLocalTime();
            if (joined.Ticks <= _s.LastMatchWhen) return;
            bool fresh = (DateTime.UtcNow - visit.Joined).TotalSeconds < 90;
            var now = _robloxNow = await LookUpRoblox(visit, withServer: true);
            string head = now.Game?.Name ?? "A Roblox game";
            string server = ServerText(now, " · ");
            var more = new List<string> { $"Roblox · {head} · playing since {joined:HH:mm}" };
            if (server.Length > 0) more.Add(ServerText(now, " · ", fps: true));
            if (now.Game != null) more.Add(GameText(now.Game, visits: true));
            if (visit.JoinSeconds > 0) more.Add($"Joined in {visit.JoinSeconds:0.0} s");

            _robloxPlaying = true;
            ShowGameResult("Roblox", head, GameText(now.Game) is { Length: > 0 } g ? g : "Playing now", more.ToArray(),
                "Roblox    " + head + (server.Length > 0 ? "    " + ServerText(now, "    ") : ""),
                joined, fresh, won: false, icon: now.Icon ?? "", say: server.Length > 0 ? server : "Playing now");
        }
        catch (Exception ex)
        {
            App.LogError("roblox join", ex);
        }
    }

    async void OnRobloxLeft(RobloxSession visit)
    {
        try
        {
            _robloxPlaying = false;
            DateTime joined = visit.Joined.ToLocalTime(), left = visit.Left.ToLocalTime();
            if (visit.Length.TotalSeconds < 45 || left.Ticks <= _s.LastMatchWhen || !_s.MatchSummary)
            {
                if (_homeCards.ContainsKey("match")) RebuildHome(); // no longer "playing now"
                return;
            }
            bool fresh = (DateTime.UtcNow - visit.Left).TotalMinutes < 2;
            var now = _robloxNow is { } known && known.Universe == visit.UniverseId ? known : await LookUpRoblox(visit, withServer: false);
            string head = now.Game?.Name ?? "A Roblox game";
            string game = GameText(now.Game);
            var more = new List<string> { $"Roblox · {head} · {visit.LengthText} · {joined:HH:mm} to {left:HH:mm}" };
            if (ServerText(now, " · ", fps: true) is { Length: > 0 } server) more.Add(server);
            if (now.Game != null) more.Add(GameText(now.Game, visits: true));
            var entry = new List<string>();
            if (visit.JoinSeconds > 0) entry.Add($"Joined in {visit.JoinSeconds:0.0} s");
            if (visit.Teleports > 0) entry.Add($"{visit.Teleports} {(visit.Teleports == 1 ? "teleport" : "teleports")}");
            if (entry.Count > 0) more.Add(string.Join(" · ", entry));

            ShowGameResult("Roblox", head, visit.LengthText + (game.Length > 0 ? " · " + game : ""), more.ToArray(),
                $"Roblox    {head}    {visit.LengthText}", left, fresh, won: false, icon: now.Icon ?? "", say: "Played " + visit.LengthText);
        }
        catch (Exception ex)
        {
            App.LogError("roblox summary", ex);
        }
    }

    // ----- any other game: which one, and for how long -----

    readonly GameWatch _games = new();

    void OnGameEnded(GameSession game)
    {
        if (!_s.MatchSummary || game.Length.TotalMinutes < 3) return; // opened and closed: not worth a word
        // Counter-Strike tells its own, fuller story when that is switched on
        if (_s.Cs2Summary && string.Equals(Path.GetFileNameWithoutExtension(game.Exe), "cs2", StringComparison.OrdinalIgnoreCase)) return;
        string span = $"{game.Started:HH:mm} to {game.Ended:HH:mm}";
        ShowGameResult(game.Name, "Played " + game.LengthText, span,
            new[] { $"{game.Name} · played {game.LengthText} · {span}" }, $"{game.Name}    played {game.LengthText}", game.Ended, fresh: true, won: false);
    }

    // ----- Counter-Strike 2: the score and your numbers, told by the game itself -----

    readonly Cs2Gsi _cs2 = new();

    /// <summary>Puts Counter-Strike's settings file in place and listens, or takes both away.</summary>
    void ApplyCs2(bool announce)
    {
        if (_selfTest) return;
        if (_s.Cs2Summary && _s.MatchSummary)
        {
            if (_s.Cs2Token.Length == 0) _s.Cs2Token = Guid.NewGuid().ToString("N");
            if (!Cs2Gsi.Install(_s.Cs2Token))
            {
                if (announce) Peek("Counter-Strike 2 wasn't found on this PC", celebrate: false, seconds: 5);
                _s.Cs2Summary = false;
                return;
            }
            bool listening = _cs2.Start(_s.Cs2Token);
            if (announce) Peek(listening ? "Counter-Strike 2 is set up. Restart the game if it's open" : "Couldn't listen for Counter-Strike 2", celebrate: false, seconds: 6);
        }
        else
        {
            _cs2.Stop();
            if (!_s.Cs2Summary) Cs2Gsi.Remove();
        }
    }

    void OnCs2Match(Cs2Match match)
    {
        if (!_s.MatchSummary) return;
        ShowGameResult("Counter-Strike 2", match.Headline, match.Detail,
            new[] { $"Counter-Strike 2 · {match.Headline} · {match.Map}", $"{match.Kills} kills · {match.Deaths} deaths · {match.Assists} assists · {match.Mvps} MVP" },
            match.Line, DateTime.Now, fresh: true, won: match.Won);
    }

    // ----- shared by every game -----

    /// <summary>
    /// A game just started or ended (or an earlier one was found at start): the Home card takes it
    /// over, whichever game it was, and a fresh one is announced. "more" is what a click on the
    /// card tells, one line after the other; "say" is the short version for the pill.
    /// </summary>
    void ShowGameResult(string game, string head, string detail, string[] more, string line, DateTime when, bool fresh, bool won, string icon = "", string? say = null)
    {
        if (when.Ticks < _s.LastMatchWhen) return; // the card already shows something newer
        _s.LastMatchWhen = when.Ticks;
        _s.LastMatchDetail = detail;
        _s.LastMatchGame = game;
        _s.LastMatchIcon = icon;
        _s.LastMatchMore = string.Join("\n", more);
        _s.LastMatchHead = ""; // so the same headline twice in a row still redraws the card
        _s.LastMatchHead = head;
        if (!fresh || !_s.MatchSummary) return; // old news: fill the card, say nothing

        if (_hiddenForFullscreen && !_userHidden) ShowMatchStrip(line);
        else Peek($"{game} · {head} · {say ?? detail}", celebrate: won, seconds: 9);
    }

    /// <summary>The game still fills the screen and the notch is tucked away: a strip at the top says it instead.</summary>
    void ShowMatchStrip(string line)
    {
        _matchStrip?.Close();
        var strip = _matchStrip = new GameHudWindow { Line = line, Opacity = 0 };
        strip.Show();
        strip.PlaceTopCentre(ChosenScreen().Bounds, _hud != null ? 40 : 14); // under the game strip when that one is up
        strip.SlideIn();
        var away = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        away.Tick += (_, _) =>
        {
            away.Stop();
            strip.FadeAway(() =>
            {
                strip.Close();
                if (_matchStrip == strip) _matchStrip = null;
            });
        };
        away.Start();
    }

    /// <summary>A click on the Home card: everything known about it, a line at a time in the pill.</summary>
    void ShowLastMatch()
    {
        if (_s.LastMatchHead.Length == 0) return;
        SetExpanded(false);
        var lines = _s.LastMatchMore.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) lines = new[] { $"{_s.LastMatchGame} · {_s.LastMatchHead} · {_s.LastMatchDetail}" };
        PeekSeries(lines);
    }

    System.Windows.Threading.DispatcherTimer? _peekSeries;

    /// <summary>Shows several lines in the pill, one after the other.</summary>
    void PeekSeries(string[] lines)
    {
        _peekSeries?.Stop();
        int at = 0;
        Peek(lines[0], celebrate: false, quiet: true, seconds: 3.6);
        if (lines.Length == 1) return;
        var timer = _peekSeries = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.4) };
        timer.Tick += (_, _) =>
        {
            if (++at >= lines.Length || _expanded)
            {
                timer.Stop();
                return;
            }
            Peek(lines[at], celebrate: false, quiet: true, seconds: 3.6);
        };
        timer.Start();
    }

    // =====================================================================
    // Other apps' notifications
    // =====================================================================

    void OnNotification(string app, string title, string body)
    {
        if (!_s.ShowNotifications) return;
        Peek(NotificationText(app, title, body), celebrate: false, quiet: true, seconds: 5);
    }

    /// <summary>"Discord · Dana: are you coming?" on one line, cut to fit the pill.</summary>
    static string NotificationText(string app, string title, string body)
    {
        static string OneLine(string s) => string.Join(" ", s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        title = OneLine(title);
        body = OneLine(body);
        string text = title.Length > 0 && body.Length > 0 ? title + ": " + body : title + body;
        if (app.Length > 0 && !text.StartsWith(app, StringComparison.OrdinalIgnoreCase))
            text = text.Length > 0 ? app + " · " + text : app;
        text = Shorten(text.Length > 0 ? text : "New notification", 64);
        // a Hebrew or Arabic message that ends in "?" or "!": the mark belongs on its left
        if (text.Any(c => c >= 0x0590 && c <= 0x08FF)) text += "\u200F";
        return text;
    }

    // =====================================================================
    // Stretch reminders
    // =====================================================================

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint Size, Time; }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Seconds since the keyboard or mouse was last touched.</summary>
    static double IdleSeconds
    {
        get
        {
            var info = new LASTINPUTINFO { Size = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) / 1000.0 : 0;
        }
    }

    void TickStretch()
    {
        var now = DateTime.Now;
        // switched off, or away from the PC for a few minutes: that already was the break
        if (!_s.StretchReminders || IdleSeconds > 180)
        {
            _activeSince = now;
            return;
        }
        if (now - _activeSince < TimeSpan.FromMinutes(_s.StretchMinutes)) return;
        if (_hiddenForFullscreen) return; // in a game or a film: say it once the notch is back

        _activeSince = now;
        Peek(StretchText(_s.StretchMinutes));
    }

    static string StretchText(int minutes)
        => minutes % 60 == 0 ? $"{minutes / 60} hour{(minutes == 60 ? "" : "s")} at the screen. Stand up and stretch"
                             : $"{minutes} minutes at the screen. Stand up and stretch";

    // =====================================================================
    // Bluetooth devices' batteries, and the "offline" dot
    // =====================================================================

    readonly HashSet<string> _batteryWarned = new(StringComparer.OrdinalIgnoreCase);

    async Task ReadDeviceBatteries() => ApplyDeviceBatteries(await DeviceBattery.ReadAsync());

    void ApplyDeviceBatteries(List<(string name, int percent)> devices)
    {
        foreach (var (name, percent) in devices)
        {
            if (percent <= 20 && _batteryWarned.Add(name))
                Peek($"{Shorten(name, 22)} battery low · {percent}%", celebrate: false);
            else if (percent > 30)
                _batteryWarned.Remove(name);
        }

        DeviceBatRow.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceBatText.Text = string.Join("   ", devices.Select(d => $"{Shorten(d.name, 18)} {d.percent}%"));
    }

    void SetOffline(bool offline)
    {
        _offline = offline;
        ShowDot(NetDot, offline && _s.HudNetwork, "No internet connection");
        if (_ready) UpdateMini();
    }

    // =====================================================================
    // Themes
    // =====================================================================

    void ApplyTheme()
    {
        var (back, border, _) = NotchThemes.Look(_s.Theme);
        var body = new SolidColorBrush(back);
        body.Freeze();
        Island.Background = body;
        ShoulderLeft.Fill = body;
        ShoulderRight.Fill = body;

        if (border is { } line)
        {
            var outline = new SolidColorBrush(line);
            outline.Freeze();
            Island.BorderBrush = outline;
        }
        else
        {
            Island.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); // Neon: the outline glows
        }

        // the quick-timer sheet covers the panel, so it is never see-through
        var cover = new SolidColorBrush(Color.FromRgb(back.R, back.G, back.B));
        cover.Freeze();
        QuickBack.Background = cover;
        QuickBack.CornerRadius = Island.CornerRadius;
    }

    // =====================================================================
    // Game mode
    // =====================================================================

    static readonly string[] NotGames =
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "arc", "vlc", "mpc-hc", "mpc-hc64", "mpc-be64", "wmplayer",
        "video.ui", "applicationframehost", "powerpnt", "netflix", "spotify", "explorer", "obs64", "zoom",
    };

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    GameHudWindow? _hud;
    int _hudCount;

    /// <summary>The foreground app looks like a browser or video player rather than a game.</summary>
    static bool ForegroundIsNotGame()
    {
        try
        {
            GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
            if (pid == 0) return true;
            using var process = Process.GetProcessById((int)pid);
            return NotGames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // protected game processes can't be opened; the name check just doesn't apply
        }
    }

    /// <summary>Shows the top strip while a game fills the screen, and takes it away after.</summary>
    void UpdateGameHud(bool fullscreenApp)
    {
        bool show = _s.GameMode && fullscreenApp && !ForegroundIsNotGame();
        if (!show)
        {
            CloseGameHud();
            return;
        }

        if (_hud == null)
        {
            _hud = new GameHudWindow();
            _hud.Show();
            _hud.PlaceOn(ChosenScreen().Bounds);
            _hudCount = 0;
        }
        if (_hudCount++ % 2 == 0) _hud.Line = GameLine(); // every two seconds is plenty
    }

    void CloseGameHud()
    {
        if (_hud == null) return;
        _hud.Close();
        _hud = null;
        _gpu.Reset();
    }

    string GameLine()
    {
        _stats.Update();
        double? gpu = _gpu.Read();
        string line = $"{Now()}    CPU {Math.Round(_stats.Cpu * 100)}%";
        if (gpu is { } g) line += $"    GPU {Math.Round(g * 100)}%";
        return line + $"    RAM {Math.Round(_stats.Memory * 100)}%";
    }

    // =====================================================================
    // Quick timer: press the notch, pull down, let go on 5, 10, or 25 minutes
    // =====================================================================

    static readonly int[] QuickMinutes = { 5, 10, 25 };

    readonly List<Border> _quickChips = new();
    Point? _dragFrom;
    bool _quickDrag;
    int _hotChip = -1;

    void BuildQuickChips()
    {
        var subtle = (Brush)FindResource("SubtleBrush");
        foreach (int minutes in QuickMinutes)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = minutes.ToString(), FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            text.Children.Add(new TextBlock { Text = "minutes", FontSize = 11, Foreground = subtle, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) });

            var chip = new Border
            {
                Width = 108,
                Height = 68,
                CornerRadius = new CornerRadius(16),
                Margin = new Thickness(6, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                Child = text,
            };
            _quickChips.Add(chip);
            QuickChips.Children.Add(chip);
        }
    }

    /// <summary>A press on the notch (outside the open tab's own content) may be the start of a pull-down.</summary>
    void QuickDragDown(MouseButtonEventArgs e)
    {
        if (!_s.ShowFocus || _hiddenForFullscreen) return;
        if (_expanded && _currentPanel != null &&
            (e.OriginalSource is not Visual source || source.IsDescendantOf(_currentPanel))) return;

        _dragFrom = e.GetPosition(Island);
        Island.CaptureMouse();
    }

    void Island_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndQuickDrag(start: false);
            return;
        }

        var now = e.GetPosition(Island);
        if (!_quickDrag)
        {
            if (now.Y - from.Y < 26 || Math.Abs(now.X - from.X) > 80) return;
            BeginQuickDrag();
        }
        SetHotChip(ChipUnder(e));
    }

    void BeginQuickDrag()
    {
        _quickDrag = true;
        _collapse.Stop();
        SetExpanded(true);
        Fx.Appear(QuickTimer, dy: -10, ms: 220);
        for (int i = 0; i < _quickChips.Count; i++) Fx.Pop(_quickChips[i], from: 0.7, ms: 380, delay: 60 + i * 50);
    }

    /// <summary>Which time the pointer is on. Below the chips still counts, so there's no need to aim carefully.</summary>
    int ChipUnder(MouseEventArgs e)
    {
        for (int i = 0; i < _quickChips.Count; i++)
        {
            var chip = _quickChips[i];
            var p = e.GetPosition(chip);
            if (p.X >= -6 && p.X <= chip.ActualWidth + 6 && p.Y >= -8 && p.Y <= chip.ActualHeight + 120) return i;
        }
        return -1;
    }

    void SetHotChip(int index)
    {
        if (index == _hotChip) return;
        _hotChip = index;
        var dark = (Brush)FindResource("OnAccentBrush");
        var subtle = (Brush)FindResource("SubtleBrush");
        var text = (Brush)FindResource("TextBrush");

        for (int i = 0; i < _quickChips.Count; i++)
        {
            var chip = _quickChips[i];
            var lines = ((StackPanel)chip.Child).Children;
            bool hot = i == index;
            if (hot) chip.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else chip.Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            ((TextBlock)lines[0]).Foreground = hot ? dark : text;
            ((TextBlock)lines[1]).Foreground = hot ? dark : subtle;
            Fx.ScaleTo(chip, hot ? 1.08 : 1, 180, Fx.Spring);
        }
        QuickHint.Text = index >= 0 ? $"Let go to start {QuickMinutes[index]} minutes" : "Let go on a time to start it";
    }

    void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragFrom != null) EndQuickDrag(start: _quickDrag && _hotChip >= 0);
    }

    void EndQuickDrag(bool start)
    {
        _dragFrom = null;
        Island.ReleaseMouseCapture();
        if (!_quickDrag) return;

        _quickDrag = false;
        int minutes = start ? QuickMinutes[_hotChip] : 0;
        SetHotChip(-1);
        Fx.Disappear(QuickTimer, dy: -8, ms: 160, done: () => { if (!_quickDrag) QuickTimer.Visibility = Visibility.Collapsed; });
        if (minutes > 0) StartQuickTimer(minutes);

        _collapse.Stop();
        _collapse.Start();
    }

    /// <summary>Starts a countdown of this many minutes, replacing whatever countdown was set.</summary>
    void StartQuickTimer(int minutes)
    {
        _countdownLeft = _countdownTotal = TimeSpan.FromMinutes(minutes);
        _countdownRunning = true;
        if (ModeTimer.IsChecked != true) ModeTimer.IsChecked = true; // the Timers tab opens on this countdown
        AfterTimerChange();
        Peek($"{minutes} minute timer started", celebrate: false, quiet: true);
    }

    // =====================================================================
    // Color picker
    // =====================================================================

    const int RecentColorsMax = 6;

    async void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (_snipping) return;
        _snipping = true;
        try
        {
            // the notch must not cover the pixels being picked
            Opacity = 0;
            await Task.Delay(140);
            var shot = SnipWindow.CaptureDesktop();
            SetExpanded(false);

            var picker = new ColorPickWindow(shot);
            picker.ShowDialog();
            Opacity = 1;

            if (picker.Picked is { } color) KeepColor(ColorPickWindow.Hex(color));
        }
        catch
        {
            Peek("Couldn't pick a color", celebrate: false, quiet: true);
        }
        finally
        {
            Opacity = 1;
            _snipping = false;
        }
    }

    /// <summary>Copies the color code and remembers it as the newest of the recent colors.</summary>
    void KeepColor(string hex)
    {
        try { Clipboard.SetText(hex); } catch { /* clipboard busy: it is still kept in the list */ }

        _s.RecentColors.RemoveAll(c => c.Equals(hex, StringComparison.OrdinalIgnoreCase));
        _s.RecentColors.Insert(0, hex);
        while (_s.RecentColors.Count > RecentColorsMax) _s.RecentColors.RemoveAt(_s.RecentColors.Count - 1);
        _s.Save();

        RebuildColorSwatches(popFirst: true);
        Peek("Copied " + hex, celebrate: false, quiet: true, seconds: 2.5);
    }

    void RebuildColorSwatches(bool popFirst = false)
    {
        ColorSwatches.Children.Clear();
        foreach (string hex in _s.RecentColors)
        {
            Color color;
            try { color = (Color)ColorConverter.ConvertFromString(hex); }
            catch { continue; }

            var swatch = new Border
            {
                Width = 15,
                Height = 15,
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 5, 0),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex + " · click to copy",
            };
            string code = hex;
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                try { Clipboard.SetText(code); } catch { }
                Fx.Pop(swatch, from: 0.6, ms: 320);
            };
            ColorSwatches.Children.Add(swatch);
        }
        if (popFirst && ColorSwatches.Children.Count > 0) Fx.Pop(ColorSwatches.Children[0], from: 0.3, ms: 420);
    }

    // =====================================================================
    // Search box: apps and files, sums, unit and money conversions, a quick timer, the web
    // =====================================================================

    readonly List<Action?> _searchActions = new();
    int _searchVersion;

    void SearchBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => _ = WarmSearch();

    /// <summary>Reads the lists of apps and files in the background, then redoes the search if one is waiting.</summary>
    async Task WarmSearch()
    {
        var reading = AppSearch.Warm();
        if (reading.IsCompleted) return;
        await reading;
        if (SearchBox.Text.Trim().Length > 0) RunSearch();
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.FlowDirection = StartsRightToLeft(SearchBox.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (_ready) RunSearch();
    }

    void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_searchActions.Count > 0) _searchActions[0]?.Invoke(); // Enter takes the top answer
            e.Handled = true;
        }
    }

    void RunSearch()
    {
        string query = SearchBox.Text.Trim();
        int version = ++_searchVersion;
        SearchItems.Children.Clear();
        _searchActions.Clear();

        bool searching = query.Length > 0;
        if (searching != (SearchScroll.Visibility == Visibility.Visible))
        {
            HomeScroll.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            if (searching) Fx.Appear(SearchScroll, dy: 6, ms: 220);
            else
            {
                SearchScroll.Visibility = Visibility.Collapsed;
                Fx.Appear(HomeScroll, dy: 6, ms: 220);
            }
        }
        if (!searching) return;
        _ = WarmSearch();

        if (_s.ShowFocus && Calc.TimerMinutes(query) is { } minutes)
        {
            AddSearchRow("", $"Start a {minutes} minute timer", "Timer", () =>
            {
                StartQuickTimer(minutes);
                SearchBox.Clear();
            });
        }

        if (Calc.TryEvaluate(query, out double value))
        {
            AddCopyRow("= " + Calc.Show(value), "Click to copy", Calc.Show(value).Replace(",", ""));
        }
        else if (Calc.TryParseQuestion(query, out double amount, out string from, out string to))
        {
            if (Calc.TryConvertUnits(amount, from, to, out string answer))
            {
                AddCopyRow("= " + answer, "Click to copy", answer);
            }
            else if (Calc.Currency(from) is { } fromCode && Calc.Currency(to) is { } toCode)
            {
                int index = _searchActions.Count;
                var title = AddSearchRow("", "Looking up the rate…", $"{fromCode} to {toCode}", null);
                _ = FillRate(title, index, version, amount, fromCode, toCode);
            }
        }

        foreach (var hit in AppSearch.Find(query, 5))
        {
            string path = hit.Path;
            string where = hit.IsApp ? "App" : Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
            AddSearchRow(LargeIcon(path), hit.Name, where, () =>
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
                SearchBox.Clear();
                Keyboard.ClearFocus();
            });
        }

        AddSearchRow("", $"Search the web for “{Shorten(query, 30)}”", "", () =>
        {
            try { Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(query)) { UseShellExecute = true }); } catch { }
            SearchBox.Clear();
            Keyboard.ClearFocus();
        });
    }

    async Task FillRate(TextBlock title, int index, int version, double amount, string from, string to)
    {
        string? answer = await Rates.ConvertAsync(amount, from, to);
        if (version != _searchVersion) return; // the user has typed something else since
        if (answer == null)
        {
            title.Text = "Couldn't get the rate right now";
            return;
        }
        title.Text = "= " + answer;
        _searchActions[index] = () => CopyAnswer(answer, title);
    }

    void AddCopyRow(string shown, string sub, string copied)
    {
        TextBlock? title = null;
        title = AddSearchRow("", shown, sub, () => CopyAnswer(copied, title!));
    }

    void CopyAnswer(string text, TextBlock title)
    {
        try { Clipboard.SetText(text); } catch { return; }
        Fx.Pop(title, from: 0.9, ms: 300);
        Peek("Copied " + Shorten(text, 30), celebrate: false, quiet: true);
    }

    /// <summary>One result line: an icon (a glyph or a picture), the answer, and a quiet note on the right.</summary>
    TextBlock AddSearchRow(object? icon, string title, string sub, Action? action)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (icon is ImageSource picture)
        {
            grid.Children.Add(new Image { Source = picture, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            var glyph = new TextBlock
            {
                Text = icon as string ?? "",
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            grid.Children.Add(glyph);
        }

        var text = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var note = new TextBlock { Text = sub, FontSize = 10.5, Foreground = (Brush)FindResource("SubtleBrush"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 110, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(note, 2);
        grid.Children.Add(note);

        var row = new Button { Style = (Style)FindResource("RowButton"), Content = grid };
        int index = _searchActions.Count;
        _searchActions.Add(action);
        row.Click += (_, _) => { if (index < _searchActions.Count) _searchActions[index]?.Invoke(); };
        SearchItems.Children.Add(row);
        return text;
    }
}
