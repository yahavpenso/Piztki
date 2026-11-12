using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Piztki.Controls;
using Piztki.Core;

namespace Piztki;

// Scripted walkthrough used for QA: "Piztki.exe --selftest <folder>" drives the notch through its
// features without touching the mouse and saves a picture of each step. It runs on throwaway
// default settings, so nothing the user has saved is read or changed.
public partial class NotchWindow
{
    bool _selfTest;
    /// <summary>
    /// PIZTKI_OFFSCREEN=1 runs the walkthrough far outside every monitor, so nothing shows up on
    /// (or takes focus from) whatever the user is doing, even a full-screen game. Pictures still work.
    /// </summary>
    bool _offscreenTest;
    bool _lyricsFrozenForTest;

    /// <summary>
    /// "Piztki.exe --perftest file": opens, closes, and switches tabs for a while and records how
    /// many frames were drawn and how much CPU it took, to compare rendering settings.
    /// </summary>
    public async Task RunPerfTestAsync(string file)
    {
        _selfTest = true;
        await Task.Delay(2500);

        int frames = 0;
        double worstGapMs = 0;
        var last = TimeSpan.Zero;
        EventHandler onFrame = (_, e) =>
        {
            var t = ((RenderingEventArgs)e).RenderingTime;
            if (t == last) return; // the event can fire twice for one frame
            if (last != TimeSpan.Zero) worstGapMs = Math.Max(worstGapMs, (t - last).TotalMilliseconds);
            last = t;
            frames++;
        };

        var process = System.Diagnostics.Process.GetCurrentProcess();
        var cpu0 = process.TotalProcessorTime;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        CompositionTarget.Rendering += onFrame;

        for (int round = 0; round < 8; round++)
        {
            SetExpanded(true);
            await Task.Delay(650);
            _tabs[(round % 6) + 1].IsChecked = true;
            await Task.Delay(550);
            TabMusic.IsChecked = true;
            await Task.Delay(550);
            SetExpanded(false);
            await Task.Delay(650);
        }

        clock.Stop();
        process.Refresh();
        double seconds = clock.Elapsed.TotalSeconds;
        double cpu = (process.TotalProcessorTime - cpu0).TotalSeconds / seconds * 100;
        string notch = $"notch: fps={frames / seconds:0.0} worst_gap={worstGapMs:0}ms cpu={cpu:0.0}%";

        // the settings window is the largest thing Piztki ever animates
        frames = 0;
        worstGapMs = 0;
        last = TimeSpan.Zero;
        clock.Restart();
        var settings = new SettingsWindow();
        settings.Show();
        await Task.Delay(900);
        for (int i = 1; i < 9; i++)
        {
            settings.Nav.SelectedIndex = i;
            await Task.Delay(600);
        }
        clock.Stop();
        CompositionTarget.Rendering -= onFrame;
        settings.Close();
        process.Refresh();

        File.WriteAllText(file,
            $"{notch} | settings: fps={frames / clock.Elapsed.TotalSeconds:0.0} worst_gap={worstGapMs:0}ms | " +
            $"private={process.PrivateMemorySize64 / 1048576}MB");
    }

    public async Task RunSelfTestAsync(string dir)
    {
        _selfTest = true;
        if (Environment.GetEnvironmentVariable("PIZTKI_OFFSCREEN") == "1")
        {
            _offscreenTest = true;
            Left = -20000;
            UpdateHidden();
        }
        Directory.CreateDirectory(dir);
        int n = 0;
        void Shot(string name) => Snap(this, Path.Combine(dir, $"{++n:00}_{name}.png"));
        var click = new RoutedEventArgs();

        await Task.Delay(1800);
        Shot("mini");

        // PIZTKI_QUICK=tools: notes, shortcuts, sound, the mic / camera dots, downloads (in a temp
        // folder), a screenshot (no picker shown), and the weekly focus chart
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "tools")
        {
            var log = new List<string>();
            SetExpanded(true);
            await Task.Delay(700);
            Shot("home");

            TabNotes.IsChecked = true;
            await Task.Delay(500);
            NoteBox.Text = "Buy milk\nCall the dentist at 4";
            await Task.Delay(200);
            NewNote();
            NoteBox.Text = "שלום, זה פתק בעברית";
            await Task.Delay(400);
            Shot("notes_second");
            log.Add($"notes count={_s.Notes.Count} index={_s.NoteIndex} first='{_s.Notes[0].Replace("\n", "|")}' rtl={NoteBox.FlowDirection}");
            NotePrev_Click(this, click);
            await Task.Delay(400);
            Shot("notes_first");
            DeleteNote();
            log.Add($"after delete count={_s.Notes.Count} title='{NoteTitle.Text}'");
            Keyboard.ClearFocus();

            TabShortcuts.IsChecked = true;
            await Task.Delay(500);
            Shot("shortcuts_empty");
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            AddShortcuts(new[] { Path.Combine(windows, "notepad.exe"), Path.Combine(windows, "System32", "calc.exe"), windows, @"C:\gone\missing.exe" });
            AddShortcuts(new[] { Path.Combine(windows, "notepad.exe") }); // a repeat must not add a second tile
            await Task.Delay(700);
            Shot("shortcuts");
            log.Add($"shortcuts count={_s.Shortcuts.Count} tiles={ShortcutItems.Children.Count}");

            TabSound.IsChecked = true;
            await Task.Delay(900);
            Shot("sound");
            log.Add($"sound rows={_soundRows.Count} names=[{string.Join("; ", _soundRows.Select(r => r.App.Name + " " + Math.Round(r.App.Volume * 100)))}] mic present={_micPresent} muted={_micMuted} text='{MicStateText.Text}'");
            log.Add("mixer error: " + Mixer.LastError);
            log.Add($"privacy real: mic={PrivacyWatch.MicrophoneUser() ?? "none"} cam={PrivacyWatch.CameraUser() ?? "none"}");

            SetPrivacy("Discord", "Zoom", announce: false);
            await Task.Delay(300);
            Shot("sound_mic_in_use");

            TabFocus.IsChecked = true;
            await Task.Delay(400);
            var day = DateTime.Now.Date;
            int[] fake = { 20, 0, 65, 45, 110, 5, 50 };
            for (int i = 0; i < 7; i++) _s.FocusHistory[day.AddDays(i - 6).ToString("yyyy-MM-dd")] = fake[i] * 60;
            ModeStats.IsChecked = true;
            await Task.Delay(1100);
            Shot("week_chart");
            log.Add($"stats title='{StatsTitle.Text}' sub='{StatsSub.Text}' bars={StatsBars.Children.Count}");
            ModeFocus.IsChecked = true;
            await Task.Delay(300);
            log.Add($"back to timer: body={TimerBody.Visibility} stats={StatsBody.Visibility}");

            // screenshot: capture for real, but pick the area in code instead of showing the picker
            TabShelf.IsChecked = true;
            await Task.Delay(400);
            var desktop = SnipWindow.CaptureDesktop();
            string saved = KeepScreenshot(desktop, new Int32Rect(100, 100, 640, 360), Path.Combine(dir, "shots"));
            await Task.Delay(700);
            Shot("shelf_with_screenshot");
            var check = new BitmapImage(new Uri(saved));
            log.Add($"screenshot desktop={desktop.PixelWidth}x{desktop.PixelHeight} file={Path.GetFileName(saved)} size={check.PixelWidth}x{check.PixelHeight} bytes={new FileInfo(saved).Length} shelf={_s.Shelf.Count}");

            // downloads: a pretend browser writing into a temp folder
            string dl = Path.Combine(dir, "downloads");
            Directory.CreateDirectory(dl);
            _downloads.Stop();
            _downloads.Start(dl);
            string temp = Path.Combine(dl, "Unconfirmed 4821.crdownload");
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                var chunk = new byte[3 << 20];
                for (int i = 0; i < 4; i++)
                {
                    stream.Write(chunk);
                    stream.Flush();
                    await Task.Delay(1000);
                    _downloads.Poll();
                    log.Add($"download tick {i}: count={_downloads.Count} bytes={_downloads.Bytes} speed={_downloads.BytesPerSec:0} pill='{DownloadText()}'");
                }
            }
            if (DownloadCardWanted != _downloadCardShown) RebuildHome();
            TabHome.IsChecked = true;
            await Task.Delay(500);
            UpdateHome();
            Shot("home_downloading");
            SetExpanded(false);
            await Task.Delay(700);
            UpdateMini();
            await Task.Delay(500);
            Shot("pill_downloading_with_dots");
            log.Add($"pill text='{MiniText.Text}' width={_miniWidth} dots mic={MicDot.Visibility} cam={CamDot.Visibility}");

            File.Move(temp, Path.Combine(dl, "holiday-photos.zip"));
            await Task.Delay(300);
            _downloads.Poll();
            log.Add($"finished: count={_downloads.Count} last={Path.GetFileName(_downloads.LastFinished ?? "none")} peek='{_peekText}'");
            await Task.Delay(500);
            Shot("pill_downloaded");
            SetExpanded(true);
            await Task.Delay(700);
            UpdateHome();
            Shot("home_downloaded");

            // a cancelled download leaves nothing behind and must not be announced
            string cancelled = Path.Combine(dl, "movie.mp4.part");
            File.WriteAllBytes(cancelled, new byte[1000]);
            await Task.Delay(300);
            _downloads.Poll();
            File.Delete(cancelled);
            await Task.Delay(300);
            string before = _downloads.LastFinished ?? "";
            _downloads.Poll();
            log.Add($"cancelled: count={_downloads.Count} lastUnchanged={before == (_downloads.LastFinished ?? "")}");
            _downloads.Stop();

            SetPrivacy(null, null, announce: false);
            log.Add($"dots off: mic={MicDot.Visibility} cam={CamDot.Visibility}");

            // every tab on: do the twelve icons and the settings button fit on one row?
            double right = _tabs[^1].TranslatePoint(new Point(_tabs[^1].ActualWidth, 0), RightCol).X;
            log.Add($"tabs right edge={right:0} of {RightCol.ActualWidth:0} (settings button needs 30)");

            var toolSettings = new SettingsWindow { ShowActivated = false };
            if (_offscreenTest)
            {
                toolSettings.WindowStartupLocation = WindowStartupLocation.Manual;
                toolSettings.Left = -20000;
                toolSettings.Top = 0;
            }
            toolSettings.Show();
            await Task.Delay(900);
            toolSettings.Nav.SelectedIndex = 2;
            await Task.Delay(700);
            toolSettings.PageScroll.ScrollToEnd();
            await Task.Delay(500);
            Snap(toolSettings, Path.Combine(dir, "settings_features.png"));
            _s.ShowNotes = _s.ShowShortcuts = _s.ShowSound = false;
            await Task.Delay(600);
            log.Add($"tabs hidden: notes={TabNotes.Visibility} shortcuts={TabShortcuts.Visibility} sound={TabSound.Visibility}");
            Shot("three_tabs_off");
            toolSettings.Close();

            File.WriteAllLines(Path.Combine(dir, "tools.txt"), log);
            return;
        }

        // PIZTKI_QUICK=1: just check the song colors and stop (for testing one change, not everything)
        // PIZTKI_QUICK=locate: press "Find my location" in the weather settings and picture the result.
        // The text file deliberately records only whether it worked, not where the user is.
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "locate")
        {
            var page = new SettingsWindow();
            page.Show();
            await Task.Delay(900);
            page.Nav.SelectedIndex = 8;
            await Task.Delay(800);
            page.LocateButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(4500);
            Snap(page, Path.Combine(dir, "locate.png"));
            File.WriteAllText(Path.Combine(dir, "locate.txt"),
                $"cityFilled={_s.WeatherCity.Length > 0} weatherOn={_s.ShowWeather} hasCoordinates={_s.WeatherLat != 0 || _s.WeatherLon != 0} " +
                $"weatherTextShown={_weather.Text.Length > 0}");
            page.Close();
            return;
        }

        // PIZTKI_QUICK=colinput: mouse wheel, clicking a line, and untimed lyrics, all inside the
        // notch with a made-up song. The click is caught here instead of really seeking the user's music.
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "colinput")
        {
            var log = new List<string>();
            SetExpanded(true);
            TabMusic.IsChecked = true;
            await Task.Delay(900);

            _lyricsFrozenForTest = true;
            MusicOn.BeginAnimation(OpacityProperty, null);
            MusicOn.Opacity = 0;
            MusicOff.Visibility = Visibility.Collapsed;
            Fx.Appear(LyricsView, dx: 0, dy: 0, ms: 1);
            LyricsEmpty.Visibility = Visibility.Collapsed;
            LyricsCol.Visibility = Visibility.Visible;
            LyricsTitle.Text = "Test song · Test artist";
            LyricsView.UpdateLayout();
            _karaoke.Stop();

            var song = Enumerable.Range(0, 14).Select(i => (TimeSpan.FromSeconds(5 + i * 3), $"Line number {i + 1} of the test song")).ToList();
            LyricsCol.SetLyrics(song, timed: true);
            await Task.Delay(300);

            void Wheel(int delta) => LyricsCol.RaiseEvent(
                new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = UIElement.MouseWheelEvent });

            // 1. follow the song to line 5 (the row list starts with the intro dots, so that is row 5)
            LyricsCol.Update(TimeSpan.FromSeconds(18), true, force: true);
            await Task.Delay(300);
            double followed = LyricsCol.ScrollOffset;
            log.Add($"rows={LyricsCol.RowCount} (14 lines + intro dots)  scroll while following line 5: {followed:0}");

            // 2. wheel down three notches: the column should move by hand
            Wheel(-120); Wheel(-120); Wheel(-120);
            await Task.Delay(200);
            double afterWheel = LyricsCol.ScrollOffset;
            log.Add($"after wheel down x3: {afterWheel:0}  moved={(afterWheel - followed):0} (expect about -180)");
            Shot("wheel_scrolled_down");

            // 3. the song moves to the next line while the user is scrolling: it must NOT yank the view back
            LyricsCol.Update(TimeSpan.FromSeconds(21), true, force: false);
            await Task.Delay(700);
            log.Add($"line changed during hand scroll: scroll={LyricsCol.ScrollOffset:0} (expect unchanged {afterWheel:0})");

            // 4. wheel up past the top: it must stop at the top, not fly off
            for (int i = 0; i < 30; i++) Wheel(120);
            await Task.Delay(200);
            log.Add($"after wheel up x30: {LyricsCol.ScrollOffset:0} (expect a small positive number, the top)");
            for (int i = 0; i < 60; i++) Wheel(-120);
            await Task.Delay(200);
            log.Add($"after wheel down x60: {LyricsCol.ScrollOffset:0} (expect to stop near the end, not keep going)");
            Shot("wheel_at_bottom");

            // 5. four seconds after the last wheel move, the next line change takes the view back to the song
            await Task.Delay(4300);
            LyricsCol.Update(TimeSpan.FromSeconds(24), true, force: false);
            await Task.Delay(900);
            log.Add($"4.3s later, next line: scroll={LyricsCol.ScrollOffset:0} (expect back near the song, around {followed - 60:0})");
            Shot("returned_to_song");

            // 6. clicking a line asks to jump to that line's start
            TimeSpan? asked = null;
            LyricsCol.SeekRequested += t => asked = t;
            var target = LyricsCol.RowPanel(9); // row 9 = line 9 (row 0 is the intro dots), which starts at 5 + 8*3 = 29s
            var center = target.TranslatePoint(new Point(40, target.ActualHeight / 2), LyricsCol);
            log.Add($"line 9 sits at y={center.Y:0} in a column {LyricsCol.ActualHeight:0} tall");
            target.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            log.Add($"click on line 9 asked to jump to: {(asked == null ? "nothing!" : asked.Value.TotalSeconds + "s")} (expect 29s)");

            // is the visible current line really what a mouse would hit there?
            LyricsCol.Update(TimeSpan.FromSeconds(24), true, force: true);
            await Task.Delay(300);
            var current = LyricsCol.RowPanel(7);
            var probe = current.TranslatePoint(new Point(30, current.ActualHeight / 2), this);
            var hit = InputHitTest(probe) as DependencyObject;
            bool inside = false;
            while (hit != null) { if (ReferenceEquals(hit, current)) { inside = true; break; } hit = VisualTreeHelper.GetParent(hit); }
            log.Add($"a real mouse at the current line would hit that line: {inside}");

            // 7. untimed lyrics: nothing highlighted, everything readable, wheel scrolls
            var plain = Enumerable.Range(0, 20).Select(i => (TimeSpan.Zero, i % 5 == 4 ? "" : $"Untimed line {i + 1} just to read")).ToList();
            LyricsCol.SetLyrics(plain, timed: false);
            await Task.Delay(300);
            LyricsCol.Update(TimeSpan.FromSeconds(30), true, force: true); // should be ignored
            double plainTop = LyricsCol.ScrollOffset;
            log.Add($"untimed: rows={LyricsCol.RowCount} opacity of rows 0/3/10 = {LyricsCol.RowOpacity(0):0.00}/{LyricsCol.RowOpacity(3):0.00}/{LyricsCol.RowOpacity(10):0.00} (expect all the same) scroll={plainTop:0}");
            Shot("untimed_top");
            Wheel(-120); Wheel(-120); Wheel(-120); Wheel(-120);
            await Task.Delay(200);
            log.Add($"untimed after wheel down x4: {LyricsCol.ScrollOffset:0} (expect about {plainTop - 240:0})");
            Shot("untimed_scrolled");

            File.WriteAllLines(Path.Combine(dir, "colinput.txt"), log);
            return;
        }

        // PIZTKI_QUICK=column: the lyrics column with made-up timed lines, so it doesn't depend on
        // what is playing and never opens anything fullscreen on the user's screen
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "column")
        {
            SetExpanded(true);
            TabMusic.IsChecked = true;
            await Task.Delay(900);

            // show the page by hand, with its own clock stopped
            _lyricsFrozenForTest = true;
            MusicOn.BeginAnimation(OpacityProperty, null);
            MusicOn.Opacity = 0;
            MusicOff.Visibility = Visibility.Collapsed;
            Fx.Appear(LyricsView, dx: 0, dy: 0, ms: 1);
            LyricsEmpty.Visibility = Visibility.Collapsed;
            LyricsCol.Visibility = Visibility.Visible;
            LyricsTitle.Text = "Test song · Test artist";
            LyricsView.UpdateLayout();
            _karaoke.Stop();
            _lyricsOpen = false;

            (double at, string text)[] song =
            {
                (6, "The first line comes in after the intro"),
                (9, "A second line that is quite a lot longer and should wrap onto two rows of text"),
                (13, "Short one"),
                (15, ""),
                (24, "Back after the break with more words"),
                (27, "שורה בעברית שמתמלאת מימין לשמאל"),
                (30, "And the last line of the test"),
            };
            LyricsCol.SetLyrics(song.Select(l => (TimeSpan.FromSeconds(l.at), l.text)).ToList(), timed: true);
            await Task.Delay(300);

            foreach (var (name, second) in new[] { ("intro_dots", 3.0), ("line_one_filling", 7.2), ("wrapped_line", 10.8), ("interlude", 19.0), ("hebrew_line", 28.2) })
            {
                LyricsCol.Update(TimeSpan.FromSeconds(second), fillAsSung: true, force: true);
                await Task.Delay(250);
                Shot("column_" + name);
            }

            // the same thing moving on its own for a second, to catch the grow-and-scroll animation mid-way
            LyricsCol.Update(TimeSpan.FromSeconds(13.1), fillAsSung: true, force: false);
            await Task.Delay(220);
            Shot("column_transition_mid");
            return;
        }

        // PIZTKI_QUICK=cinema: the new lyrics column in the notch, then the fullscreen cinema view
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "cinema")
        {
            var log = new List<string>();
            await Task.Delay(2000); // lyrics need a moment to arrive
            SetExpanded(true);
            TabMusic.IsChecked = true;
            OpenLyrics(true);
            await Task.Delay(1800);
            Shot("notch_lyrics_page");
            log.Add($"notch: timed={_lyrics.HasLyrics} plain={_lyrics.HasPlain} lines={_lyrics.Lines.Count} columnHasRows={LyricsCol.HasRows}");

            // an untimed song, shown as plain scrollable text
            LyricsCol.SetLyrics(new[] { "First line of a song nobody timed", "Second line", "", "Third line after a blank" }
                .Select(l => (TimeSpan.Zero, l)).ToList(), timed: false);
            await Task.Delay(500);
            Shot("notch_untimed_lyrics");
            _columnKey = "";
            UpdateLyricsView();

            var app = (App)Application.Current;
            app.ShowCinema();
            await Task.Delay(3500);
            var cinema = Application.Current.Windows.OfType<LyricsWindow>().FirstOrDefault();
            log.Add($"cinema open={cinema != null} notchHidden={_hiddenForFullscreen} size={cinema?.ActualWidth:0}x{cinema?.ActualHeight:0}");
            if (cinema != null)
            {
                var root = (FrameworkElement)cinema.Content;
                log.Add($"cinema root opacity={root.Opacity:0.00} visible={root.IsVisible} loaded={cinema.IsLoaded} size={root.ActualWidth:0}x{root.ActualHeight:0} state={cinema.WindowState}");
                Snap(cinema, Path.Combine(dir, "cinema.png"));
                await Task.Delay(2500);
                Snap(cinema, Path.Combine(dir, "cinema_later.png"));
                var process = System.Diagnostics.Process.GetCurrentProcess();
                log.Add($"memory with cinema open: {process.PrivateMemorySize64 / 1048576} MB");
                cinema.Close();
            }
            await Task.Delay(1500);
            log.Add($"after closing: notchHidden={_hiddenForFullscreen} renderMode={RenderOptions.ProcessRenderMode}");
            foreach (int wait in new[] { 0, 3000, 5000 })
            {
                await Task.Delay(wait);
                GC.Collect();
                var after = System.Diagnostics.Process.GetCurrentProcess();
                log.Add($"memory {wait / 1000 + (wait == 0 ? 1.5 : 0):0.#}s later: {after.PrivateMemorySize64 / 1048576} MB");
            }
            File.WriteAllLines(Path.Combine(dir, "cinema.txt"), log);
            return;
        }

        // PIZTKI_QUICK=timing: what pace was learned for the playing song, and how the lines are timed
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "timing")
        {
            await Task.Delay(2500); // give the lyrics time to arrive
            var log = new List<string>();
            var lines = _lyrics.Lines;
            log.Add($"song=\"{_media.Title}\" lines={lines.Count} pace={_lyricPace:0.000} s/syllable");
            for (int i = 0; i < Math.Min(lines.Count - 1, 14); i++)
            {
                if (lines[i].text.Length == 0) continue;
                double gap = (lines[i + 1].time - lines[i].time).TotalSeconds;
                var (start, end) = LineTiming(i);
                double oldEstimate = Math.Min(gap * 0.95, Math.Max(1.2, lines[i].text.Length * 0.085));
                log.Add($"syll={LyricTiming.Syllables(lines[i].text),2} gap={gap,5:0.00}s  new fill={(end - start).TotalSeconds,5:0.00}s  old fill={oldEstimate,5:0.00}s  \"{lines[i].text}\"");
            }

            var map = new LyricTiming.WordMap("I tried to numb away the pain");
            log.Add("word map for \"I tried to numb away the pain\": " +
                    string.Join(" ", new[] { 0.1, 0.25, 0.5, 0.75, 0.9 }.Select(t => $"{t:0.00}->{map.CharsAt(t):0.00}")));
            log.Add($"syllables: hello={LyricTiming.Syllables("hello")} beautiful={LyricTiming.Syllables("beautiful")} " +
                    $"שלום={LyricTiming.Syllables("שלום")} tried={LyricTiming.Syllables("tried")}");
            File.WriteAllLines(Path.Combine(dir, "timing.txt"), log);
            return;
        }

        // PIZTKI_QUICK=cloudfade: do the cloud colors blend from one song's palette to the next?
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "cloudfade")
        {
            var log = new List<string>();
            SetExpanded(true);
            TabMusic.IsChecked = true;
            OpenLyrics(true);
            await Task.Delay(2500);
            log.Add($"start: {_cloudCenters?[0].Color}");

            // pretend the next song has a red, green, and blue cover
            _media.ArtColors.Clear();
            _media.ArtColors.AddRange(new[] { Color.FromRgb(220, 40, 40), Color.FromRgb(40, 200, 80), Color.FromRgb(40, 90, 230) });
            UpdateClouds();
            foreach (int ms in new[] { 100, 400, 400, 400, 400, 400 })
            {
                await Task.Delay(ms);
                log.Add($"+{ms}ms: {_cloudCenters?[0].Color}");
            }
            Shot("clouds_new_colors");
            File.WriteAllLines(Path.Combine(dir, "cloudfade.txt"), log);
            return;
        }

        // PIZTKI_QUICK=musicpage: the Music settings page, and the Features page it was split out of
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "musicpage")
        {
            var page = new SettingsWindow();
            page.Show();
            await Task.Delay(900);
            page.Nav.SelectedIndex = 3;
            await Task.Delay(1000);
            Snap(page, Path.Combine(dir, "settings_music.png"));
            page.Nav.SelectedIndex = 2;
            await Task.Delay(1000);
            Snap(page, Path.Combine(dir, "settings_features.png"));
            page.Close();
            return;
        }

        // PIZTKI_QUICK=pillfill: the lyric line in the collapsed pill filling as it is sung
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "pillfill")
        {
            var log = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(700);
                Shot($"pill_{i}");
                log.Add($"kind={(_miniKind.Length > 22 ? _miniKind[..22] : _miniKind)} filling={_miniFilling} timer={_karaoke.IsEnabled} " +
                        $"lit={_miniLit?.Offset:0.00} playing={_media.IsPlaying} lines={_lyrics.Lines.Count}");
            }
            File.WriteAllLines(Path.Combine(dir, "pillfill.txt"), log);
            return;
        }

        // PIZTKI_QUICK=lyrics: the sing-along page, plus the next-event card and the calendar's labelled buttons
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "lyrics")
        {
            var log = new List<string>();
            _s.Events.Add(new CalEvent { Title = "Dentist", When = DateTime.Now.Date.AddDays(1).AddHours(14) });
            SetExpanded(true);
            await Task.Delay(1300);
            Shot("home_next_event");

            TabCal.IsChecked = true;
            await Task.Delay(900);
            Shot("calendar_events_side");
            ShowCalendarSide(world: true, animate: false);
            await Task.Delay(500);
            Shot("calendar_world_side");
            ShowCalendarSide(world: false, animate: false);

            TabMusic.IsChecked = true;
            await Task.Delay(900);
            Shot("music_with_mic_button");
            OpenLyrics(true);
            log.Add($"lines={_lyrics.Lines.Count} hasRows={LyricsCol.HasRows} playing={_media.IsPlaying} colors={_media.ArtColors.Count}");
            await Task.Delay(1500);
            Shot("lyrics_page");
            await Task.Delay(1500);
            Shot("lyrics_page_later");
            log.Add($"karaokeTimer={_karaoke.IsEnabled}");
            OpenLyrics(false);
            await Task.Delay(700);
            Shot("back_to_player");
            File.WriteAllLines(Path.Combine(dir, "lyrics.txt"), log);
            return;
        }

        // PIZTKI_QUICK=calback: is the "back to events" button in the world view really clickable?
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "calback")
        {
            var log = new List<string>();
            SetExpanded(true);
            TabCal.IsChecked = true;
            await Task.Delay(1200);

            string Under(FrameworkElement target)
            {
                var center = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), this);
                var hit = InputHitTest(center) as DependencyObject;
                var chain = new List<string>();
                while (hit != null && chain.Count < 7)
                {
                    chain.Add(hit is FrameworkElement fe && fe.Name.Length > 0 ? $"{hit.GetType().Name}#{fe.Name}" : hit.GetType().Name);
                    hit = VisualTreeHelper.GetParent(hit);
                }
                return $"at {center.X:0},{center.Y:0}: " + string.Join(" < ", chain);
            }

            var globe = CalEvents.Children.OfType<Button>().First();
            log.Add("globe button " + Under(globe));
            globe.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(800);
            log.Add($"after globe: events={CalEvents.Visibility} world={CalWorld.Visibility}");

            var back = ((Grid)CalWorld.Children[0]).Children.OfType<Button>().First();
            log.Add($"back button size {back.ActualWidth:0}x{back.ActualHeight:0} visible={back.IsVisible} enabled={back.IsEnabled} " + Under(back));
            Shot("world_view");
            back.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(800);
            log.Add($"after back: events={CalEvents.Visibility} world={CalWorld.Visibility} eventsOpacity={CalEvents.Opacity:0.00}");
            Shot("after_back");
            File.WriteAllLines(Path.Combine(dir, "calback.txt"), log);
            return;
        }

        // PIZTKI_QUICK=events: calendar events and their reminders
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "events")
        {
            var log = new List<string>();
            foreach (var sample in new[] { "9", "930", "9:30", "09.30", "14:05", "25:00", "abc" })
                log.Add($"time \"{sample}\" -> {(TryParseTime(sample, out var parsed) ? parsed.ToString(@"hh\:mm") : "rejected")}");

            SetExpanded(true);
            TabCal.IsChecked = true;
            await Task.Delay(1200);
            Shot("calendar_empty_day");

            // through the same boxes the user types in
            EventTitleBox.Text = "Dentist";
            EventTimeBox.Text = "23:59";
            AddEventFromBoxes();
            EventTitleBox.Text = "Call grandma";
            EventTimeBox.Text = "2358";
            AddEventFromBoxes();
            await Task.Delay(700);
            Shot("calendar_two_events_today");

            SelectDay(DateTime.Now.Date.AddDays(3));
            EventTitleBox.Text = "Trip";
            EventTimeBox.Text = "8";
            AddEventFromBoxes();
            await Task.Delay(700);
            Shot("calendar_other_day");
            log.Add($"events saved: {_s.Events.Count}: " + string.Join("; ", _s.Events.Select(ev => $"{ev.Title} {ev.When:MM-dd HH:mm}")));

            CalView_Click(this, click);
            await Task.Delay(600);
            Shot("calendar_world_view");
            CalView_Click(this, click);
            SetExpanded(false);
            await Task.Delay(800);

            // reminders: one event ten minutes out, one starting in two seconds
            _s.Events.Add(new CalEvent { Title = "Stand-up meeting", When = DateTime.Now.AddMinutes(9.8) });
            await Task.Delay(1600);
            Shot("reminder_10_minutes");
            log.Add($"early reminder text: \"{_peekText}\"");
            _s.Events.Add(new CalEvent { Title = "Lunch", When = DateTime.Now.AddSeconds(2), NotifiedEarly = true });
            await Task.Delay(3400);
            Shot("reminder_now");
            log.Add($"start reminder text: \"{_peekText}\"");
            log.Add("flags: " + string.Join("; ", _s.Events.Select(ev => $"{ev.Title} early={ev.NotifiedEarly} now={ev.Notified}")));

            File.WriteAllLines(Path.Combine(dir, "events.txt"), log);
            return;
        }

        // PIZTKI_QUICK=attached: the notch joined to the top edge, closed and open, then floating
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "attached")
        {
            _s.NotchAttached = true;
            await Task.Delay(1200);
            Shot("attached_pill");
            SetExpanded(true);
            await Task.Delay(1200);
            Shot("attached_open");
            SetExpanded(false);
            _s.NotchAttached = false;
            await Task.Delay(1200);
            Shot("floating_pill");
            return;
        }

        // PIZTKI_QUICK=batch3: the display / style / popups / calendar additions
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "batch3")
        {
            var log = new List<string>();

            Peek("Caps Lock on", quiet: true);
            await Task.Delay(500);
            Shot("popup_capslock");
            Peek("Keyboard: " + LanguageName(0x040D), quiet: true);
            await Task.Delay(500);
            Shot("popup_language");
            log.Add($"language names: {LanguageName(0x040D)} / {LanguageName(0x0409)}  caps now={Native.CapsLockOn}  layout now={Native.KeyboardLanguage:X4}");
            await Task.Delay(2000);

            _s.MinimalNotch = true;
            await Task.Delay(900);
            Shot("minimal_pill");
            _s.MinimalNotch = false;
            _s.NotchAttached = true;
            await Task.Delay(900);
            Shot("attached_pill");

            _s.CountdownName = "Vacation";
            _s.CountdownDate = DateTime.Now.AddDays(89).ToString("yyyy-MM-dd");
            SetExpanded(true);
            TabCal.IsChecked = true;
            await Task.Delay(1300);
            Shot("calendar_attached");
            ShiftMonth(1);
            await Task.Delay(700);
            Shot("calendar_next_month");
            _s.NotchAttached = false;
            SetExpanded(false);
            await Task.Delay(700);

            var screens = System.Windows.Forms.Screen.AllScreens;
            log.Add($"screens: {screens.Length}  chosen={ChosenScreen().DeviceName}");
            Native.GetWindowRect(_hwnd, out var before);
            if (screens.Length > 1)
            {
                var other = screens.First(x => !x.Primary);
                _s.ScreenName = other.DeviceName;
                await Task.Delay(1400);
                Native.GetWindowRect(_hwnd, out var moved);
                log.Add($"moved to {other.DeviceName}: window left {before.Left} -> {moved.Left}, screen spans {other.Bounds.Left}..{other.Bounds.Right}");
                _s.ScreenName = "";
                await Task.Delay(1400);
                Native.GetWindowRect(_hwnd, out var back);
                log.Add($"back on main: window left {back.Left}");
            }

            var page = new SettingsWindow();
            page.Show();
            await Task.Delay(900);
            foreach (var (index, name) in new[] { (1, "notch"), (9, "calendar"), (10, "shelf") })
            {
                page.Nav.SelectedIndex = index;
                await Task.Delay(1000);
                Snap(page, Path.Combine(dir, $"{++n:00}_settings_{name}.png"));
            }
            page.Close();

            File.WriteAllLines(Path.Combine(dir, "batch3.txt"), log);
            return;
        }

        // PIZTKI_QUICK=home: picture the home tab, then a card click, plus a sheet of candidate icons
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "home")
        {
            AddTodo("Buy milk");
            AddToShelf(new[] { Environment.ProcessPath ?? "" });
            SetExpanded(true);
            await Task.Delay(1300);
            Shot("home");
            ((System.Windows.Controls.Button)HomeItems.Children[1]).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(800);
            Shot("home_card_clicked");

            var sheet = new WrapPanel { Width = 640, Margin = new Thickness(12) };
            foreach (var code in new[] { 0xE8B7, 0xE838, 0xE8F1, 0xED41, 0xED43, 0xE7B8, 0xEC50, 0xE8DA, 0xE82D, 0xE80F, 0xE719, 0xE7BF, 0xE74C, 0xF12B, 0xE8A5, 0xE7C3, 0xE8E5, 0xE78C, 0xE74E, 0xE896 })
            {
                var cell = new StackPanel { Width = 62, Margin = new Thickness(0, 0, 0, 10) };
                cell.Children.Add(new TextBlock { Text = char.ConvertFromUtf32(code), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 26, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
                cell.Children.Add(new TextBlock { Text = code.ToString("X4"), FontSize = 11, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
                sheet.Children.Add(cell);
            }
            var glyphs = new Window
            {
                Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0D)), Child = sheet },
                SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = 0,
            };
            glyphs.Show();
            await Task.Delay(500);
            Snap(glyphs, Path.Combine(dir, "glyphs.png"));
            glyphs.Close();
            return;
        }

        // PIZTKI_QUICK=extras: search / calculator, quick timer, notifications, device batteries, the
        // offline dot, themes, the game strip, picked colors, and their settings rows
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "extras")
        {
            var log = new List<string>();
            var en = System.Globalization.CultureInfo.InvariantCulture;

            foreach (var sum in new[] { "12*(3+4)", "2^10", "(1+2)*3-4/8", "sqrt 81 + 1", "10 % 4", "hello", "42", "7/0" })
                log.Add($"calc '{sum}' -> {(Calc.TryEvaluate(sum, out double v) ? Calc.Show(v) : "not a sum")}");
            foreach (var ask in new[] { "5 km to miles", "100 f to c", "2.5 kg in lb", "90 min to hours", "3 gb to mb", "5 km to kg" })
                log.Add($"units '{ask}' -> {(Calc.TryParseQuestion(ask, out double qty, out string f, out string t) && Calc.TryConvertUnits(qty, f, t, out string ans) ? ans : "no answer")}");
            log.Add($"timer words: '10m'={Calc.TimerMinutes("10m")} 'timer 25'={Calc.TimerMinutes("timer 25")} '10'={Calc.TimerMinutes("10")?.ToString() ?? "none"} '500m'={Calc.TimerMinutes("500m")?.ToString() ?? "none"}");
            log.Add($"money words: usd={Calc.Currency("usd")} shekel={Calc.Currency("שקל")} km={Calc.Currency("km") ?? "none"}");

            SetExpanded(true);
            await Task.Delay(800);
            Shot("home_with_search");

            SearchBox.Text = "12*(3+4)";
            await Task.Delay(400);
            Shot("search_sum");
            log.Add($"search rows for a sum: {SearchItems.Children.Count}  cards hidden={HomeScroll.Visibility}");

            SearchBox.Text = "5 km to miles";
            await Task.Delay(300);
            Shot("search_units");

            SearchBox.Text = "20 usd to ils";
            await Task.Delay(3500);
            Shot("search_money");
            log.Add("money row: " + ((TextBlock)((Grid)((Button)SearchItems.Children[0]).Content).Children[1]).Text);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            await AppSearch.Warm();
            log.Add($"app list: {AppSearch.AppCount} apps, {AppSearch.FileCount} files, read in {clock.ElapsedMilliseconds} ms");
            SearchBox.Text = "note";
            await Task.Delay(500);
            Shot("search_apps");
            log.Add("rows for 'note': " + string.Join(" | ", SearchItems.Children.OfType<Button>().Select(b => ((TextBlock)((Grid)b.Content).Children[1]).Text)));

            SearchBox.Text = "10m";
            await Task.Delay(300);
            Shot("search_timer");
            _searchActions[0]?.Invoke();
            await Task.Delay(400);
            log.Add($"after '10m' + Enter: running={_countdownRunning} left={Format(_countdownLeft)} box='{SearchBox.Text}' cards back={HomeScroll.Visibility}");
            _countdownRunning = false;

            // the pull-down quick timer, driven the way the mouse would
            SetExpanded(false);
            await Task.Delay(500);
            _dragFrom = new Point(60, 10);
            BeginQuickDrag();
            await Task.Delay(700);
            Shot("quick_timer_open");
            SetHotChip(2);
            await Task.Delay(400);
            Shot("quick_timer_on_25");
            EndQuickDrag(start: true);
            await Task.Delay(600);
            log.Add($"quick timer: running={_countdownRunning} left={Format(_countdownLeft)} overlay={QuickTimer.Visibility} mode={_mode}");
            SetExpanded(false);
            await Task.Delay(2200);
            Shot("pill_with_timer");
            log.Add($"pill text='{MiniText.Text}'");
            _countdownRunning = false;
            _peekUntil = DateTime.MinValue;
            UpdateMini();

            // a notification from another app
            OnNotification("Discord", "Dana", "are you coming tonight?\nwe start at 8");
            await Task.Delay(700);
            Shot("pill_notification");
            log.Add($"notification pill='{MiniText.Text}' width={_miniWidth}");
            OnNotification("WhatsApp", "אמא", "מתי אתה חוזר?");
            await Task.Delay(700);
            Shot("pill_notification_hebrew");
            _notifications.Poll();
            await Task.Delay(2500);
            log.Add($"real notification listener status: '{_notifications.Status}' (empty = working)");
            _peekUntil = DateTime.MinValue;

            log.Add($"stretch text: '{StretchText(60)}' / '{StretchText(45)}' / '{StretchText(120)}'  idle now={IdleSeconds:0.0}s");

            // batteries of Bluetooth devices
            var real = await DeviceBattery.ReadAsync();
            log.Add($"real device scan: {DeviceBattery.LastScanned} devices looked at, {real.Count} report a battery, error='{DeviceBattery.LastError}'");
            ApplyDeviceBatteries(new List<(string, int)> { ("WH-1000XM4", 80), ("Xbox Wireless Controller", 15) });
            await Task.Delay(700);
            Shot("pill_device_battery_low");
            log.Add($"device warning pill='{MiniText.Text}'");
            SetExpanded(true);
            TabSystem.IsChecked = true;
            await Task.Delay(1300);
            Shot("system_with_devices");
            SetExpanded(false);
            _peekUntil = DateTime.MinValue;

            SetOffline(true);
            await Task.Delay(800);
            Shot("pill_offline_dot");
            log.Add($"offline dot={NetDot.Visibility}");
            SetOffline(false);

            // picked colors
            var desktop = SnipWindow.CaptureDesktop();
            var sample = ColorPickWindow.ColorAt(desktop, (desktop.PixelWidth / 2, desktop.PixelHeight / 2));
            log.Add($"pixel in the middle of the desktop picture: {ColorPickWindow.Hex(sample)}");
            KeepColor("#3A7BD5");
            KeepColor("#FF8800");
            KeepColor("#3A7BD5");
            log.Add("recent colors: " + string.Join(" ", _s.RecentColors) + $"  swatches={ColorSwatches.Children.Count}");
            _peekUntil = DateTime.MinValue;
            SetExpanded(true);
            TabShelf.IsChecked = true;
            await Task.Delay(1200);
            Shot("shelf_with_colors");

            // themes, in the floating style where the outline shows
            TabHome.IsChecked = true;
            _s.NotchAttached = false;
            foreach (var theme in NotchThemes.Names)
            {
                _s.Theme = theme;
                _s.Accent = NotchThemes.Look(theme).accent;
                SetExpanded(true);
                await Task.Delay(1100);
                Shot("theme_" + theme.ToLowerInvariant() + "_open");
                SetExpanded(false);
                await Task.Delay(900);
                Shot("theme_" + theme.ToLowerInvariant() + "_pill");
            }
            _s.Theme = "Midnight";
            _s.Accent = NotchThemes.Look("Midnight").accent;

            // the game strip, kept off-screen
            var strip = new GameHudWindow { Left = -20000, Top = 40 };
            strip.Show();
            strip.Line = GameLine();
            await Task.Delay(1500);
            clock.Restart();
            strip.Line = GameLine();
            long cost = clock.ElapsedMilliseconds;
            await Task.Delay(400);
            Snap(strip, Path.Combine(dir, "game_strip.png"));
            log.Add($"game strip: '{strip.Line}'  one refresh took {cost} ms  gpu error='{_gpu.LastError}'  not-a-game check={ForegroundIsNotGame()}");
            strip.Close();

            var page = new SettingsWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0 };
            page.Show();
            await Task.Delay(900);
            page.PageScroll.ScrollToEnd();
            await Task.Delay(600);
            Snap(page, Path.Combine(dir, "settings_general_end.png"));
            page.Nav.SelectedIndex = 5;
            await Task.Delay(1100);
            Snap(page, Path.Combine(dir, "settings_style.png"));
            page.Nav.SelectedIndex = 2;
            await Task.Delay(900);
            page.PageScroll.ScrollToEnd();
            await Task.Delay(600);
            Snap(page, Path.Combine(dir, "settings_features_end.png"));
            page.Close();

            File.WriteAllLines(Path.Combine(dir, "extras.txt"), log);
            return;
        }

        // PIZTKI_QUICK=discord: can we reach the Discord app, the voice row in the Sound tab, the settings page
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "discord")
        {
            var log = new List<string>();
            using (var probe = new DiscordRpc())
            {
                bool bad = await probe.ConnectAsync("12ab");
                log.Add($"not a number: connected={bad} status='{probe.Status}'");
                // an id nobody owns: reaching "Discord doesn't know this" proves the pipe and the framing work
                bool made = await probe.ConnectAsync("100000000000000001");
                log.Add($"made-up id: connected={made} status='{probe.Status}'");
            }

            // the answer that crashed the app: Discord's error replies carry a number where a text was expected
            var error = System.Text.Json.Nodes.JsonNode.Parse("{\"cmd\":\"AUTHORIZE\",\"evt\":\"ERROR\",\"data\":{\"code\":5000,\"message\":\"Unknown Error\"},\"nonce\":\"x\",\"flag\":true}");
            log.Add($"error reply read safely: code='{error?["data"]?["code"].Text()}' message='{error?["data"]?["message"].Text()}' missing='{error?["data"]?["nope"].Text() ?? "null"}' object='{error?["data"].Text() ?? "null"}' flag={error?["flag"].Flag()} notflag={error?["nonce"].Flag()?.ToString() ?? "null"}");

            _pomo = PomoState.Focus; _pomoRunning = true; _pomoLeft = TimeSpan.FromMinutes(18);
            var p1 = PresenceNow();
            log.Add($"presence while focusing: '{p1.details}' / '{p1.state}' ends in {(p1.ends - DateTimeOffset.Now)?.TotalMinutes:0.0} min");
            _pomo = PomoState.Idle; _pomoRunning = false;
            var p2 = PresenceNow();
            log.Add($"presence otherwise: '{p2.details}' / '{p2.state}'");
            log.Add($"status line with no id: '{DiscordStatus}'");

            SetExpanded(true);
            TabSound.IsChecked = true;
            await Task.Delay(1200);
            DiscordRow.Visibility = Visibility.Visible;
            ShowDiscordRow("Fortnite squad", 4, new List<string> { "Dana" }, muted: false, deafened: false);
            await Task.Delay(500);
            Shot("sound_discord_talking");
            ShowDiscordRow("", 0, new List<string>(), muted: true, deafened: false);
            await Task.Delay(300);
            Shot("sound_discord_muted_no_channel");

            var page = new SettingsWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0 };
            page.Show();
            await Task.Delay(900);
            page.Nav.SelectedIndex = 11;
            await Task.Delay(1100);
            Snap(page, Path.Combine(dir, "settings_discord.png"));
            for (int step = 0; step < 5; step++)
            {
                page.SetGuideForTest(step);
                await Task.Delay(450);
                Snap(page, Path.Combine(dir, $"guide_{step + 1}.png"));
            }
            page.SetGuideForTest(0);
            page.PageScroll.ScrollToEnd();
            await Task.Delay(500);
            Snap(page, Path.Combine(dir, "settings_discord_end.png"));
            page.Nav.SelectedIndex = 12;
            await Task.Delay(900);
            Snap(page, Path.Combine(dir, "settings_about_still_there.png"));
            page.Close();

            File.WriteAllLines(Path.Combine(dir, "discord.txt"), log);
            return;
        }

        // PIZTKI_QUICK=fortnite: read the real replays on this PC, then the Home card, the pill, and the lobby strip
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "fortnite")
        {
            var log = new List<string>();
            var folder = new DirectoryInfo(FortniteReplay.Folder);
            log.Add($"folder exists={folder.Exists}");
            FortniteMatch? newest = null;
            if (folder.Exists)
                foreach (var f in folder.EnumerateFiles("*.replay").OrderBy(f => f.LastWriteTimeUtc))
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var m = FortniteReplay.Read(f.FullName);
                    log.Add($"{f.Name}  {f.Length / 1048576.0:0.0} MB  read in {clock.ElapsedMilliseconds} ms  -> " +
                            (m == null ? "no result" : $"{m.Headline} | {m.Detail} | assists {m.Assists} revives {m.Revives} taken {m.DamageTaken} {m.Kilometres:0.0} km {m.Minutes:0.0} min"));
                    if (m != null) newest = m;
                }
            var match = newest ?? new FortniteMatch("made-up.replay", DateTime.Now, 3, 100, 4, 1, 612, 0.27, 0, 180, 3.4, 17);

            // the watcher: nothing new, so nothing is read twice
            int told = 0;
            var watch = new FortniteWatch { Done = folder.Exists ? folder.EnumerateFiles("*.replay").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.Name ?? "" : "" };
            watch.Finished += (_, _) => told++;
            for (int i = 0; i < 4; i++) { watch.Poll(); await Task.Delay(300); }
            log.Add($"watcher with nothing new announced {told} time(s)");
            // ...and with the newest one not seen yet: read once, flagged as old news
            bool? fresh = null;
            var watch2 = new FortniteWatch();
            watch2.Finished += (_, f) => { told += 10; fresh = f; };
            for (int i = 0; i < 5; i++) { watch2.Poll(); await Task.Delay(400); }
            log.Add($"watcher with one unseen: told={told} fresh={fresh} done='{watch2.Done}'");

            OnMatchFinished(match, fresh: true);
            await Task.Delay(1200);
            Shot("pill");
            log.Add($"pill says '{_peekText}'  saved head='{_s.LastMatchHead}' detail='{_s.LastMatchDetail}'");
            _peekUntil = DateTime.Now;
            SetExpanded(true);
            await Task.Delay(1400);
            Shot("home_card");
            log.Add($"home card present={_homeCards.ContainsKey("match")}");
            ShowLastMatch();
            await Task.Delay(1200);
            Shot("pill_details");
            log.Add($"details '{_peekText}'");

            foreach (var shown in new[] { match, match with { Place = 1 } })
            {
                var strip = new GameHudWindow { Line = shown.Line, Left = -20000, Top = 40 };
                strip.Show();
                strip.PlaceTopCentre(new System.Drawing.Rectangle(-30000, 0, 1920, 1080));
                strip.SlideIn();
                await Task.Delay(900);
                Snap(strip, Path.Combine(dir, shown.Won ? "strip_win.png" : "strip.png"));
                log.Add($"strip '{strip.Line}' at left={strip.Left:0} width={strip.ActualWidth:0} opacity={strip.Opacity:0.00}");
                bool gone = false;
                strip.FadeAway(() => gone = true);
                await Task.Delay(700);
                log.Add($"strip faded away={gone}");
                strip.Close();
            }

            // ----- Roblox -----
            // the real log on this PC: the last visit it holds
            RobloxSession? real = null;
            var realWatch = new RobloxWatch();
            realWatch.Finished += v => real = v;
            var readClock = System.Diagnostics.Stopwatch.StartNew();
            realWatch.Poll();
            log.Add($"real roblox log read in {readClock.ElapsedMilliseconds} ms: " + (real == null ? "no finished visit" : $"universe {real.UniverseId} {real.Joined.ToLocalTime():dd.MM HH:mm} to {real.Left.ToLocalTime():HH:mm} = {real.LengthText}"));
            if (real != null) log.Add($"its name from roblox.com: '{await RobloxInfo.NameOf(real.UniverseId)}'");
            log.Add($"a game number that does not exist: '{await RobloxInfo.NameOf(1)}'");
            if (real != null)
            {
                log.Add($"real visit: job id {real.JobId.Length} chars, server address {(real.ServerIp.Length > 0 ? "found" : "missing")}, joined in {real.JoinSeconds:0.0} s, teleports {real.Teleports}");
                var g = await RobloxInfo.Game(real.UniverseId);
                log.Add($"game info: by '{g?.Creator}' playing {g?.Playing} visits {g?.Visits} max {g?.MaxPlayers} {g?.UpdatedText}");
                log.Add($"server is in: '{await RobloxInfo.Where(real.ServerIp)}'   a home address gives: '{await RobloxInfo.Where("192.168.1.20")}'");
                string? icon = await RobloxInfo.Icon(real.UniverseId);
                log.Add($"picture saved: {icon != null && File.Exists(icon)} ({(icon != null ? new FileInfo(icon).Length : 0)} bytes)");
            }
            log.Add($"short numbers: {RobloxInfo.Short(950)} {RobloxInfo.Short(12400)} {RobloxInfo.Short(1234567)} {RobloxInfo.Short(3200000000)}");

            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                try
                {
                    using var list = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("https://games.roblox.com/v1/games/920587237/servers/Public?limit=100"));
                    var data = list.RootElement.GetProperty("data");
                    string id = data[data.GetArrayLength() - 1].GetProperty("id").GetString() ?? "";
                    var found = await RobloxInfo.Server(920587237, id);
                    log.Add($"server list: looked for the last of {data.GetArrayLength()} servers -> {(found == null ? "not found" : $"{found.Playing}/{found.MaxPlayers} players, ~{found.Ping} ms, {found.Fps:0} fps")}");
                    log.Add($"a server that is not listed: {(await RobloxInfo.Server(920587237, "00000000-0000-0000-0000-000000000000") == null ? "not found (as it should be)" : "found?!")}");
                }
                catch (Exception ex)
                {
                    log.Add("server list could not be tried: " + ex.Message);
                }
            }

            // a made-up log that grows the way the real one does
            string fake = Path.Combine(dir, "0.1_Player_TEST_last.log");
            string T(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            long universe = real?.UniverseId ?? 10648443086;
            var t0 = DateTime.Now.AddMinutes(-83);
            File.WriteAllText(fake,
                $"{T(t0)},1.0,0380,6 [FLog::Output] ! Joining game 'aaaa' place 111 at 1.2.3.4\n" +
                $"{T(t0)},1.0,0380,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:111, join_time:1.4, universeid:{universe}, referral_page:Home\n" +
                $"{T(t0)},1.0,0380,7 [FLog::Network] UDMUX Address = {(real?.ServerIp.Length > 0 ? real.ServerIp : "128.116.44.33")}, Port = 55304 | RCC Server Address = 10.1.2.3, Port = 55304\n" +
                $"{T(t0.AddMinutes(10))},1.0,0380,6 [FLog::Output] ! Joining game 'bbbb' place 222 at 1.2.3.4\n" +
                $"{T(t0.AddMinutes(10))},1.0,0380,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:222, join_time:0, universeid:{universe}, referral_page:\n");
            var visits = new List<RobloxSession>();
            _roblox.FileOverride = fake;
            var fakeWatch = new RobloxWatch { FileOverride = fake };
            fakeWatch.Finished += v => visits.Add(v);
            var joins = new List<RobloxSession>();
            fakeWatch.Joined += v => joins.Add(v);
            fakeWatch.Poll();
            log.Add($"still in the game (after a teleport): finished visits={visits.Count} joins said={joins.Count} teleports so far={(joins.Count > 0 ? joins[0].Teleports : -1)} address known={(joins.Count > 0 && joins[0].ServerIp.Length > 0)}");
            File.AppendAllText(fake, $"{T(DateTime.Now)},1.0,23d4,6 [FLog::SingleSurfaceApp] leaveUGCGameInternal\n");
            fakeWatch.Poll();
            fakeWatch.Poll();
            log.Add($"after leaving: finished visits={visits.Count}" + (visits.Count > 0 ? $" place={visits[0].PlaceId} length='{visits[0].LengthText}'" : ""));
            // a quick in-and-out, and a second game straight after the first without a goodbye line
            File.AppendAllText(fake,
                $"{T(DateTime.Now.AddMinutes(-5))},1.0,0380,6 [FLog::Output] ! Joining game 'cccc' place 333 at 1.2.3.4\n" +
                $"{T(DateTime.Now.AddMinutes(-5))},1.0,0380,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:333, join_time:1, universeid:777, referral_page:\n" +
                $"{T(DateTime.Now)},1.0,0380,6 [FLog::Output] ! Joining game 'dddd' place 444 at 1.2.3.4\n" +
                $"{T(DateTime.Now)},1.0,0380,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:444, join_time:1, universeid:888, referral_page:\n");
            fakeWatch.Poll();
            log.Add($"joined another game without leaving: finished visits={visits.Count} last universe={visits[^1].UniverseId} length='{visits[^1].LengthText}'");

            // the whole way through the app: joining first...
            _peekUntil = DateTime.Now;
            SetExpanded(false);
            OnRobloxJoined(joins[0] with { Joined = DateTime.UtcNow.AddSeconds(-5) });
            await Task.Delay(7000);
            Shot("roblox_join_pill");
            log.Add($"join pill '{_peekText}'  playing={_robloxPlaying}");
            log.Add($"join card: head='{_s.LastMatchHead}' detail='{_s.LastMatchDetail}' picture={_s.LastMatchIcon.Length > 0}");
            foreach (var l in _s.LastMatchMore.Split('\n')) log.Add("   more: " + l);
            _peekUntil = DateTime.Now;
            SetExpanded(true);
            await Task.Delay(1400);
            Shot("roblox_join_card");
            SetExpanded(false);
            // ...then leaving
            _peekUntil = DateTime.Now;
            OnRobloxLeft(visits[0] with { Joined = DateTime.UtcNow.AddMinutes(-83), Left = DateTime.UtcNow });
            await Task.Delay(3500);
            Shot("roblox_pill");
            log.Add($"roblox pill '{_peekText}'  game='{_s.LastMatchGame}' head='{_s.LastMatchHead}' detail='{_s.LastMatchDetail}'");
            _peekUntil = DateTime.Now;
            SetExpanded(true);
            await Task.Delay(1400);
            Shot("roblox_card");
            ShowLastMatch();
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(i == 0 ? 900 : 3400);
                log.Add($"roblox details {i + 1}: '{_peekText}'");
                if (i == 1) Shot("roblox_details_2");
            }
            // an older Fortnite match must not push the newer Roblox visit off the card
            OnMatchFinished(match, fresh: false);
            log.Add($"after an older fortnite match: card still '{_s.LastMatchGame} / {_s.LastMatchHead}'");
            {
                var strip = new GameHudWindow { Line = $"Roblox    {_s.LastMatchHead}    {visits[0].LengthText}", Left = -20000, Top = 40 };
                strip.Show();
                strip.PlaceTopCentre(new System.Drawing.Rectangle(-30000, 0, 1920, 1080));
                strip.SlideIn();
                await Task.Delay(900);
                Snap(strip, Path.Combine(dir, "strip_roblox.png"));
                strip.Close();
            }

            _s.MatchSummary = false;
            await Task.Delay(300);
            log.Add($"switched off: card present={_homeCards.ContainsKey("match")}");
            File.WriteAllLines(Path.Combine(dir, "fortnite.txt"), log);
            return;
        }

        // PIZTKI_QUICK=hud: the game strip sits in the middle of the top edge and stays there as its text changes
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "hud")
        {
            var log = new List<string>();
            var screen = new System.Drawing.Rectangle(-30000, 0, 1920, 1080);
            var strip = new GameHudWindow { Left = -20000, Top = 40 };
            strip.Show();
            strip.Line = GameLine();
            strip.PlaceOn(screen);
            await Task.Delay(500);
            double scale = VisualTreeHelper.GetDpi(strip).DpiScaleX;
            double Middle() => strip.Left + strip.ActualWidth / 2 - (screen.Left + screen.Width / 2.0) / scale;
            log.Add($"'{strip.Line}' width={strip.ActualWidth:0} top={strip.Top:0} off-centre by {Middle():0.0}");
            strip.Line = "01:47";
            await Task.Delay(400);
            log.Add($"short text: width={strip.ActualWidth:0} off-centre by {Middle():0.0}");
            strip.Line = GameLine() + "    and something much longer";
            await Task.Delay(400);
            log.Add($"long text: width={strip.ActualWidth:0} off-centre by {Middle():0.0}");
            var match = new GameHudWindow { Line = "Fortnite    #2 of 80    1 elim", Left = -20000 };
            match.Show();
            match.PlaceTopCentre(screen, 40);
            await Task.Delay(400);
            log.Add($"match strip under it: top={match.Top:0}, game strip bottom={strip.Top + strip.ActualHeight:0}");
            match.Close();
            strip.Close();
            File.WriteAllLines(Path.Combine(dir, "hud.txt"), log);
            return;
        }

        // PIZTKI_QUICK=film: one short clip per feature for the README, as numbered frames in one
        // folder per clip. While a feature is filmed every other feature is switched off, so its
        // tab is the only one in the row. Everything shown is made up: the song, the tasks, the
        // messages, the files, the games.
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "film")
        {
            _s.Sounds = false;
            _s.MascotMoods = false;
            _s.NotchAttached = false;
            _s.MatchSummary = false;
            UpdateMood();

            async Task Film(string name, double seconds, Action<double>? each = null, Window? of = null)
            {
                var frames = new List<BitmapSource>();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    each?.Invoke(clock.Elapsed.TotalSeconds);
                    frames.Add(FilmFrame(of ?? this));
                    double wait = frames.Count * (1000.0 / 12) - clock.ElapsedMilliseconds;
                    await Task.Delay((int)Math.Max(5, wait));
                }
                string folder = Path.Combine(dir, name);
                Directory.CreateDirectory(folder);
                for (int i = 0; i < frames.Count; i++)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frames[i]));
                    using var file = File.Create(Path.Combine(folder, $"{i:000}.png"));
                    encoder.Save(file);
                }
            }

            // switches every feature off except the ones named; "home" keeps the Home tab's icon
            async Task Only(params string[] keep)
            {
                bool K(string n) => keep.Contains(n) || keep.Contains("all");
                TabHome.IsChecked = true;
                _s.ShowMusic = K("music");
                _s.ShowLyrics = false; // the made-up song has no lyrics to find; the lyrics clip shows its own
                _s.ShowFocus = K("focus");
                _s.ShowTodo = K("todo");
                _s.ShowWater = K("water");
                _s.ShowShelf = K("shelf");
                _s.ShowClipboard = K("clip");
                _s.ShowSystem = K("system");
                _s.ShowCalendar = K("cal");
                _s.ShowNotes = K("notes");
                _s.ShowSound = false; // lists the apps really running on this PC
                _s.ShowShortcuts = K("shortcuts");
                _s.ShowSearch = K("search");
                TabHome.Visibility = K("home") ? Visibility.Visible : Visibility.Collapsed;
                _peekUntil = DateTime.Now;
                UpdateMini();
                await Task.Delay(500);
            }

            BitmapSource Cover(Color top, Color bottom, Color moon)
            {
                var paint = new DrawingVisual();
                using (var dc = paint.RenderOpen())
                {
                    dc.DrawRectangle(new LinearGradientBrush(top, bottom, 90), null, new Rect(0, 0, 300, 300));
                    dc.DrawEllipse(new SolidColorBrush(moon), null, new Point(150, 150), 58, 58);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb((byte)((top.R + bottom.R) / 2), (byte)((top.G + bottom.G) / 2), (byte)((top.B + bottom.B) / 2))), null, new Point(176, 136), 50, 50);
                }
                var bitmap = new RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(paint);
                bitmap.Freeze();
                return bitmap;
            }
            var violet = Cover(Color.FromRgb(0x8E, 0x7C, 0xFF), Color.FromRgb(0x2B, 0x3F, 0xB8), Color.FromRgb(0xF1, 0xEE, 0xFF));
            void Song() => _media.ShowDemo("Paper Lanterns", "The Night Kites", violet, TimeSpan.FromSeconds(204), TimeSpan.FromSeconds(41));
            Song();
            SetBars(true);
            await Task.Delay(1500);

            // ---------- music ----------
            await Only("music");
            {
                TabMusic.IsChecked = true;
                SetExpanded(false);
                await Task.Delay(900);
                var film = Film("music", 7);
                await Task.Delay(1800);
                SetExpanded(true);
                await film;
            }

            // ---------- lyrics ----------
            {
                _s.ShowLyrics = true;
                await Task.Delay(600);
                _lyricsFrozenForTest = true;
                MusicOn.BeginAnimation(OpacityProperty, null);
                MusicOn.Opacity = 0;
                MusicOff.Visibility = Visibility.Collapsed;
                Fx.Appear(LyricsView, dx: 0, dy: 0, ms: 1);
                LyricsEmpty.Visibility = Visibility.Collapsed;
                LyricsCol.Visibility = Visibility.Visible;
                LyricsTitle.Text = "Paper Lanterns · The Night Kites";
                LyricsView.UpdateLayout();
                _karaoke.Stop();
                _lyricsOpen = false;
                (double at, string text)[] song =
                {
                    (1.0, "We lit the paper lanterns one by one"),
                    (3.6, "And watched them climb above the sleeping town"),
                    (6.4, "Little lights"),
                    (8.0, "Carry what we couldn't say out loud"),
                    (10.8, "Over the rooftops, over the sea"),
                    (13.4, "Until the morning comes for you and me"),
                };
                LyricsCol.SetLyrics(song.Select(l => (TimeSpan.FromSeconds(l.at), l.text)).ToList(), timed: true);
                await Task.Delay(300);
                await Film("lyrics", 9, t => LyricsCol.Update(TimeSpan.FromSeconds(0.6 + t * 1.45), fillAsSung: true, force: false));
                // back to the player page
                LyricsView.Visibility = Visibility.Collapsed;
                MusicOn.Opacity = 1;
                _lyricsFrozenForTest = false;
                _s.ShowLyrics = false;
            }

            // ---------- song colors: the glow follows the cover ----------
            {
                await Task.Delay(400);
                var film = Film("song-colors", 8);
                (string title, string artist, BitmapSource cover)[] songs =
                {
                    ("Sea Glass", "Harbor Lights", Cover(Color.FromRgb(0x2E, 0xD3, 0xB7), Color.FromRgb(0x0B, 0x5E, 0x7A), Color.FromRgb(0xE9, 0xFF, 0xF8))),
                    ("Peach Soda", "Summer Static", Cover(Color.FromRgb(0xFF, 0x8F, 0xB1), Color.FromRgb(0xC2, 0x3B, 0x7A), Color.FromRgb(0xFF, 0xF1, 0xE6))),
                    ("Golden Hour", "The Slow Trains", Cover(Color.FromRgb(0xFF, 0xC8, 0x57), Color.FromRgb(0xC9, 0x6A, 0x1B), Color.FromRgb(0xFF, 0xF8, 0xE1))),
                };
                foreach (var (title, artist, cover) in songs)
                {
                    await Task.Delay(1800);
                    _media.ShowDemo(title, artist, cover, TimeSpan.FromSeconds(187), TimeSpan.FromSeconds(12));
                }
                await Task.Delay(1800);
                Song();
                await film;
            }

            // ---------- the mascot dancing on the beat ----------
            {
                await Task.Delay(600);
                BigMascot.Dancing = true;
                double nextBeat = 0;
                await Film("mascot", 5, t =>
                {
                    if (t < nextBeat) return;
                    BigMascot.Beat(0.5);
                    nextBeat += 0.5;
                });
            }

            // ---------- timers ----------
            await Only("focus");
            {
                TabFocus.IsChecked = true;
                await Task.Delay(700);
                var film = Film("timers", 8);
                await Task.Delay(500);
                PomoStart_Click(this, click);
                await Task.Delay(2600);
                ModeTimer.IsChecked = true;
                await Task.Delay(600);
                QuickAdd_Click(new Button { Tag = "5" }, click);
                PomoStart_Click(this, click);
                await Task.Delay(2200);
                ModeStopwatch.IsChecked = true;
                await Task.Delay(400);
                PomoStart_Click(this, click);
                await film;
                PomoReset_Click(this, click);
                ModeTimer.IsChecked = true;
                PomoReset_Click(this, click);
                ModeFocus.IsChecked = true;
                PomoReset_Click(this, click);
            }

            // ---------- focus chart ----------
            {
                var day = DateTime.Now.Date;
                int[] minutes = { 20, 0, 65, 45, 110, 5, 50 };
                for (int i = 0; i < 7; i++) _s.FocusHistory[day.AddDays(i - 6).ToString("yyyy-MM-dd")] = minutes[i] * 60;
                await Task.Delay(500);
                var film = Film("focus-chart", 5);
                await Task.Delay(900);
                ModeStats.IsChecked = true;
                await film;
                ModeFocus.IsChecked = true;
            }

            // ---------- quick timer: pull down from the notch ----------
            {
                SetExpanded(false);
                await Task.Delay(1000);
                var film = Film("quick-timer", 7);
                await Task.Delay(900);
                BeginQuickDrag();
                await Task.Delay(1100);
                foreach (int chip in new[] { 0, 1, 2, 1 })
                {
                    SetHotChip(chip);
                    await Task.Delay(650);
                }
                EndQuickDrag(start: true);
                await Task.Delay(700);
                SetExpanded(false);
                await film;
                _countdownRunning = false;
                PomoReset_Click(this, click);
                _peekUntil = DateTime.Now;
                UpdateMini();
            }

            // ---------- to-do ----------
            await Only("todo");
            {
                SetExpanded(true);
                TabTodo.IsChecked = true;
                await Task.Delay(700);
                var film = Film("todo", 7);
                await Task.Delay(500);
                AddTodo("Finish the science project");
                await Task.Delay(900);
                AddTodo("Feed the cat");
                await Task.Delay(900);
                AddTodo("Practice guitar");
                await Task.Delay(1100);
                _s.Todos[0].Done = true;
                CompleteTodo(_s.Todos[0]);
                await film;
            }

            // ---------- calendar ----------
            await Only("cal");
            {
                TabCal.IsChecked = true;
                await Task.Delay(900);
                var film = Film("calendar", 7);
                await Task.Delay(1200);
                AddEvent("Movie night", DateTime.Now.Date.AddHours(20));
                await Task.Delay(1800);
                AddEvent("Football practice", DateTime.Now.Date.AddHours(17).AddMinutes(30));
                await film;
            }

            // ---------- water ----------
            await Only("water");
            {
                TabWater.IsChecked = true;
                await Task.Delay(700);
                var film = Film("water", 6);
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(800);
                    WaterAdd_Click(this, click);
                }
                await film;
            }

            // ---------- shelf ----------
            await Only("shelf");
            {
                TabShelf.IsChecked = true;
                string shelf = Path.Combine(dir, "shelf-files");
                Directory.CreateDirectory(shelf);
                await Task.Delay(700);
                var film = Film("shelf", 6.5);
                foreach (string name in new[] { "holiday-photos.zip", "homework.pdf", "song-idea.mp3", "poster.png" })
                {
                    await Task.Delay(1100);
                    string path = Path.Combine(shelf, name);
                    File.WriteAllBytes(path, new byte[2048]);
                    AddToShelf(new[] { path });
                }
                await film;
            }

            // ---------- clipboard ----------
            await Only("clip");
            {
                TabClip.IsChecked = true;
                _clips.Clear();
                RebuildClip();
                await Task.Delay(700);
                var film = Film("clipboard", 6.5);
                foreach (string text in new[] { "Meet at the skate park at 5", "#8E7CFF", "https://example.com/recipes/pancakes", "Don't forget the tickets!" })
                {
                    await Task.Delay(1100);
                    AddClip(text);
                }
                await film;
            }

            // ---------- system ----------
            await Only("system");
            {
                TabSystem.IsChecked = true;
                ApplyDeviceBatteries(new List<(string, int)> { ("Headphones", 80), ("Game controller", 45) });
                _peekUntil = DateTime.Now;
                await Task.Delay(900);
                await Film("system", 6);
            }

            // ---------- notes ----------
            await Only("notes");
            {
                TabNotes.IsChecked = true;
                await Task.Delay(700);
                var film = Film("notes", 7.5);
                string noteText = "Ideas for the weekend\n- bike ride to the lake\n- fix the treehouse ladder";
                await Task.Delay(500);
                for (int i = 1; i <= noteText.Length; i++)
                {
                    NoteBox.Text = noteText[..i];
                    await Task.Delay(70);
                }
                await film;
            }

            // ---------- shortcuts ----------
            await Only("shortcuts");
            {
                TabShortcuts.IsChecked = true;
                await Task.Delay(700);
                var film = Film("shortcuts", 6);
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                foreach (string path in new[] { Path.Combine(windows, "notepad.exe"), Path.Combine(windows, "System32", "calc.exe"), Path.Combine(windows, "System32", "mspaint.exe"), windows })
                {
                    await Task.Delay(1000);
                    AddShortcuts(new[] { path });
                }
                await film;
            }

            // ---------- search, calculator, converter ----------
            await Only("search", "home");
            {
                TabHome.IsChecked = true;
                await Task.Delay(800);
                var film = Film("search", 9);
                await Task.Delay(600);
                foreach (string typed in new[] { "128 * 4", "5 km to miles", "72 f to c" })
                {
                    for (int i = 1; i <= typed.Length; i++)
                    {
                        SearchBox.Text = typed[..i];
                        await Task.Delay(95);
                    }
                    await Task.Delay(1500);
                    if (typed != "72 f to c") SearchBox.Text = "";
                    await Task.Delay(250);
                }
                await film;
                SearchBox.Text = "";
            }

            // ---------- themes: the whole notch, in each of the four looks ----------
            await Only("all", "home");
            {
                TabHome.IsChecked = true;
                await Task.Delay(900);
                var film = Film("themes", 8);
                foreach (var theme in new[] { "Neon", "Glass", "Pastel", "Midnight" })
                {
                    await Task.Delay(1700);
                    _s.Theme = theme;
                    _s.Accent = NotchThemes.Look(theme).accent;
                }
                await film;
            }

            // ---------- from here on: things that show in the pill ----------
            await Only();
            SetExpanded(false);
            await Task.Delay(1200);

            // downloads
            {
                string dl = Path.Combine(dir, "downloads");
                Directory.CreateDirectory(dl);
                _downloads.Stop();
                _downloads.Start(dl);
                var film = Film("downloads", 8);
                await Task.Delay(700);
                string temp = Path.Combine(dl, "Unconfirmed 4821.crdownload");
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    var chunk = new byte[3 << 20];
                    for (int i = 0; i < 4; i++)
                    {
                        stream.Write(chunk);
                        stream.Flush();
                        await Task.Delay(900);
                        _downloads.Poll();
                        UpdateMini();
                    }
                }
                File.Move(temp, Path.Combine(dl, "holiday-photos.zip"));
                await Task.Delay(300);
                _downloads.Poll();
                await film;
                _downloads.Stop();
                _s.ShowDownloads = false; // or its card would linger on Home in the next clips
                _peekUntil = DateTime.Now;
                UpdateMini();
                await Task.Delay(900);
            }

            // microphone and camera dots
            {
                var film = Film("privacy-dots", 7);
                await Task.Delay(900);
                SetPrivacy("Voice chat", null, announce: true);
                await Task.Delay(2600);
                _peekUntil = DateTime.Now;
                UpdateMini();
                await Task.Delay(700);
                SetPrivacy("Voice chat", "Camera", announce: true);
                await film;
                SetPrivacy(null, null, announce: false);
                _peekUntil = DateTime.Now;
                UpdateMini();
                await Task.Delay(900);
            }

            // notifications from other apps
            {
                var film = Film("notifications", 8);
                await Task.Delay(700);
                OnNotification("Discord", "Dana", "are you coming tonight? we start at 8");
                await Task.Delay(3500);
                OnNotification("WhatsApp", "Mom", "dinner is ready");
                await film;
                _peekUntil = DateTime.Now;
                UpdateMini();
                await Task.Delay(900);
            }

            // stretch reminder
            {
                var film = Film("stretch", 5.5);
                await Task.Delay(900);
                Peek(StretchText(60), celebrate: false, seconds: 4);
                await film;
                _peekUntil = DateTime.Now;
                UpdateMini();
                await Task.Delay(900);
            }

            // small status popups
            {
                var film = Film("popups", 7);
                await Task.Delay(700);
                Peek("Caps Lock on", quiet: true);
                await Task.Delay(2000);
                Peek("Caps Lock off", quiet: true);
                await Task.Delay(2000);
                SetOffline(true);
                await film;
                SetOffline(false);
                _peekUntil = DateTime.Now;
                UpdateMini();
                await Task.Delay(900);
            }

            // game summary: a Fortnite result, then joining a Roblox game, then the card on Home
            {
                _s.MatchSummary = true;
                TabHome.Visibility = Visibility.Visible;
                var film = Film("game-summary", 11);
                await Task.Delay(600);
                ShowGameResult("Fortnite", "Victory Royale", "6 elims · 958 dmg · 31% hit",
                    new[] { "Fortnite · Victory Royale · 6 elims · 958 dmg · 31% hit" }, "", DateTime.Now.AddMinutes(-1), fresh: true, won: true);
                await Task.Delay(3600);
                _peekUntil = DateTime.Now;
                UpdateMini();
                await Task.Delay(500);
                _robloxPlaying = true;
                ShowGameResult("Roblox", "Obby Islands", "12.4K playing · by Kite Studio",
                    new[] { "Roblox · Obby Islands" }, "", DateTime.Now, fresh: true, won: false, say: "Frankfurt, Germany · 28/35 players · ~56 ms");
                await Task.Delay(3600);
                _peekUntil = DateTime.Now;
                SetExpanded(true);
                await film;
                _robloxPlaying = false;
                SetExpanded(false);
            }

            // game mode: the little strip, which is a window of its own
            {
                var strip = new GameHudWindow { Left = -20000, Top = 40 };
                strip.Show();
                strip.Line = GameLine();
                await Task.Delay(1200);
                strip.Line = GameLine();
                double next = 1;
                await Film("game-mode", 5, t =>
                {
                    if (t < next) return;
                    strip.Line = GameLine();
                    next += 1;
                }, of: strip);
                strip.Close();
            }

            File.WriteAllText(Path.Combine(dir, "film.txt"), "done");
            return;
        }

        // PIZTKI_QUICK=games: the summary for any game (its name, how long), and Counter-Strike 2's own
        // report, fed with the same kind of messages the game sends. Nothing is written into the real game folder.
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "games")
        {
            var log = new List<string>();

            // names
            string? cfg = Cs2Gsi.FindCfgFolder();
            log.Add($"counter-strike cfg folder found={cfg != null}");
            if (cfg != null)
            {
                string exe = Path.GetFullPath(Path.Combine(cfg, "..", "..", "bin", "win64", "cs2.exe"));
                log.Add($"cs2.exe exists={File.Exists(exe)} name='{GameWatch.NameFor(exe, "")}' in a game library={GameWatch.InGameLibrary(exe)}");
                string common = Path.GetFullPath(Path.Combine(cfg, "..", "..", "..", ".."));
                foreach (string folder in Directory.GetDirectories(common).Take(8))
                    log.Add($"   steam folder '{Path.GetFileName(folder)}' -> '{GameWatch.NameFor(Path.Combine(folder, "game.exe"), "")}'");
            }
            log.Add($"java game by its window title: '{GameWatch.NameFor(@"C:\Games\jre\bin\javaw.exe", "Minecraft 1.21.4 - Singleplayer")}'");
            log.Add($"unknown program, no title: '{GameWatch.NameFor(@"D:\Stuff\CoolGame-Win64-Shipping.exe", "")}'  in a library={GameWatch.InGameLibrary(@"D:\Stuff\CoolGame-Win64-Shipping.exe")}");
            log.Add($"epic path in a library={GameWatch.InGameLibrary(@"C:\Program Files\Epic Games\GTAV\GTA5.exe")}");
            var watch = new GameWatch();
            int ended = 0;
            watch.Ended += _ => ended++;
            for (int i = 0; i < 3; i++) { watch.Poll(_hwnd); await Task.Delay(200); }
            log.Add($"three polls of the real desktop: no crash, sessions ended={ended}");

            // a finished session, all the way to the pill and the card
            OnGameEnded(new GameSession("Subnautica 2", @"E:\x\Subnautica2.exe", DateTime.Now.AddMinutes(-80), DateTime.Now));
            await Task.Delay(1200);
            Shot("game_pill");
            log.Add($"pill '{_peekText}'  card: game='{_s.LastMatchGame}' head='{_s.LastMatchHead}' detail='{_s.LastMatchDetail}'");
            string before = _s.LastMatchHead;
            OnGameEnded(new GameSession("Quick Look", @"E:\x\q.exe", DateTime.Now.AddMinutes(-1), DateTime.Now));
            log.Add($"a one-minute session changes nothing: {_s.LastMatchGame == "Subnautica 2" && _s.LastMatchHead == before}");
            _peekUntil = DateTime.Now;
            SetExpanded(true);
            await Task.Delay(1400);
            Shot("game_card");
            SetExpanded(false);

            // Counter-Strike: the settings file, then messages over the real local connection
            string fakeCfg = Path.Combine(dir, "cfg");
            Directory.CreateDirectory(fakeCfg);
            log.Add($"settings file written to a test folder={Cs2Gsi.Install("secret-word", fakeCfg)}");
            log.Add(File.ReadAllText(Path.Combine(fakeCfg, "gamestate_integration_piztki.cfg")).Replace("\n", " | "));
            Cs2Gsi.Remove(fakeCfg);
            log.Add($"removed again={!File.Exists(Path.Combine(fakeCfg, "gamestate_integration_piztki.cfg"))}");
            log.Add($"map names: {Cs2Gsi.MapName("de_mirage")}, {Cs2Gsi.MapName("de_dust2")}, {Cs2Gsi.MapName("cs_office")}, {Cs2Gsi.MapName("workshop/123/de_cache")}");

            var matches = new List<Cs2Match>();
            using var gsi = new Cs2Gsi();
            gsi.MatchEnded += m => Dispatcher.BeginInvoke(() => matches.Add(m));
            const int port = 47399;
            log.Add($"listening={gsi.Start("secret-word", port)}");
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string Message(string token, string phase, int ct, int t, string who, string team, int k, int d, int a, int mvp) =>
                "{\"provider\":{\"name\":\"Counter-Strike 2\",\"appid\":730,\"steamid\":\"111\"},\"map\":{\"mode\":\"competitive\",\"name\":\"de_mirage\",\"phase\":\"" + phase +
                "\",\"round\":21,\"team_ct\":{\"score\":" + ct + "},\"team_t\":{\"score\":" + t + "}},\"player\":{\"steamid\":\"" + who + "\",\"name\":\"x\",\"team\":\"" + team +
                "\",\"match_stats\":{\"kills\":" + k + ",\"assists\":" + a + ",\"deaths\":" + d + ",\"mvps\":" + mvp + ",\"score\":40}},\"auth\":{\"token\":\"" + token + "\"}}";
            async Task Send(string body)
            {
                using var answer = await http.PostAsync($"http://127.0.0.1:{port}/", new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                if (!answer.IsSuccessStatusCode) log.Add("the listener answered " + (int)answer.StatusCode);
            }
            await Send(Message("secret-word", "live", 9, 12, "111", "T", 17, 11, 4, 3));
            await Send(Message("wrong-word", "gameover", 0, 99, "111", "T", 99, 99, 99, 99));       // somebody else on this PC: ignored
            await Task.Delay(200);
            log.Add($"a message with the wrong word ended matches={matches.Count}");
            await Send(Message("secret-word", "live", 9, 12, "222", "CT", 30, 2, 1, 6));             // dead and watching a teammate... or an enemy
            await Send(Message("secret-word", "live", 9, 13, "111", "T", 18, 12, 4, 3));
            await Send(Message("secret-word", "gameover", 9, 13, "222", "CT", 31, 2, 1, 6));          // the last word comes while watching someone else
            await Send(Message("secret-word", "gameover", 9, 13, "222", "CT", 31, 2, 1, 6));          // and is repeated
            await Task.Delay(400);
            log.Add($"matches ended={matches.Count}" + (matches.Count > 0 ? $" -> {matches[0].Headline} | {matches[0].Detail} | mvps {matches[0].Mvps} | mode {matches[0].Mode}" : ""));
            await Send("{\"provider\":{\"steamid\":\"111\"},\"player\":{\"steamid\":\"111\"},\"auth\":{\"token\":\"secret-word\"}}"); // back in the menu
            await Send(Message("secret-word", "live", 0, 0, "111", "CT", 0, 0, 0, 0));
            await Send(Message("secret-word", "gameover", 5, 13, "111", "CT", 6, 15, 2, 0));
            await Task.Delay(400);
            log.Add($"after a second match: ended={matches.Count}" + (matches.Count > 1 ? $" -> {matches[1].Headline} | {matches[1].Detail}" : ""));
            gsi.Stop();

            if (matches.Count > 0)
            {
                _peekUntil = DateTime.Now;
                OnCs2Match(matches[0]);
                await Task.Delay(1200);
                Shot("cs2_pill");
                log.Add($"cs2 pill '{_peekText}'  card label game='{_s.LastMatchGame}' head='{_s.LastMatchHead}' detail='{_s.LastMatchDetail}'");
                _peekUntil = DateTime.Now;
                SetExpanded(true);
                await Task.Delay(1400);
                Shot("cs2_card");
                var strip = new GameHudWindow { Line = matches[0].Line, Left = -20000, Top = 40 };
                strip.Show();
                strip.PlaceTopCentre(new System.Drawing.Rectangle(-30000, 0, 1920, 1080));
                strip.SlideIn();
                await Task.Delay(900);
                Snap(strip, Path.Combine(dir, "cs2_strip.png"));
                strip.Close();
            }
            File.WriteAllLines(Path.Combine(dir, "games.txt"), log);
            return;
        }

        // PIZTKI_QUICK=bpm: what tempo is found in whatever is playing right now, and are the beats evenly spaced?
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "bpm")
        {
            var log = new List<string>();
            SetBars(true);
            var beats = new List<double>();
            var edge = new BeatEdge();
            BeatService.TraceTempo = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var cpu0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            int second = 0;
            while (clock.Elapsed.TotalSeconds < 30)
            {
                await Task.Delay(15);
                if (_beatService.TryGetBeat(out double phase, out double period))
                {
                    if (edge.Step(phase, period)) beats.Add(clock.Elapsed.TotalSeconds);
                }
                if ((int)clock.Elapsed.TotalSeconds > second)
                {
                    second = (int)clock.Elapsed.TotalSeconds;
                    log.Add($"t={second,2}s  bpm={SongBpm:0.0}  raw={_beatService.RawBpm:0.0}  beats so far={beats.Count}");
                    if (second % 5 == 0) log.Add("   " + BeatService.Trace);
                }
            }
            var gaps = beats.Zip(beats.Skip(1), (x, y) => y - x).ToList();
            if (gaps.Count > 2)
                log.Add($"gaps between beats: shortest={gaps.Min():0.000}s longest={gaps.Max():0.000}s average={gaps.Average():0.000}s (= {60 / gaps.Average():0.0} bpm)");
            double cpu = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds / clock.Elapsed.TotalSeconds * 100;
            log.Add($"song: '{_media.Title}' by '{_media.Artist}'  playing={_media.IsPlaying}  cpu during the test={cpu:0.0}% of one core");

            // the dance itself: a few beats on the big mascot
            SetExpanded(true);
            await Task.Delay(900);
            BigMascot.Dancing = true;
            for (int i = 0; i < 4; i++)
            {
                BigMascot.Beat(0.5);
                await Task.Delay(i == 3 ? 270 : 500);
            }
            Shot("mascot_mid_beat");
            File.WriteAllLines(Path.Combine(dir, "bpm.txt"), log);
            return;
        }

        // PIZTKI_QUICK=dots: the microphone / camera dots in the pill, with the clock and in the minimal pill
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "dots")
        {
            bool minimal = _s.MinimalNotch, attached = _s.NotchAttached;
            foreach (bool style in new[] { false, true })
            {
            _s.NotchAttached = style;
            await Task.Delay(500);
            _s.MinimalNotch = false;
            SetPrivacy("Game", "Chat", announce: false);
            await Task.Delay(900);
            Shot("clock_both");
            SetPrivacy("Game", null, announce: false);
            await Task.Delay(900);
            Shot("clock_mic");
            _s.MinimalNotch = true;
            SetPrivacy("Game", "Chat", announce: false);
            UpdateMini();
            await Task.Delay(900);
            Shot("minimal_both");
            SetPrivacy(null, "Chat", announce: false);
            await Task.Delay(900);
            Shot("minimal_cam");
            }
            _s.MinimalNotch = minimal;
            _s.NotchAttached = attached;
            await Task.Delay(800);
            return;
        }

        // PIZTKI_QUICK=smooth: the mouse wheel glides instead of jumping, in the notch and in settings
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "smooth")
        {
            var log = new List<string>();
            void Wheel(UIElement on, int delta) => on.RaiseEvent(
                new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = UIElement.MouseWheelEvent });
            async Task<string> Trace(ScrollViewer view, int ms)
            {
                var points = new List<string>();
                for (int t = 0; t < ms; t += 40)
                {
                    await Task.Delay(40);
                    points.Add(view.VerticalOffset.ToString("0"));
                }
                return string.Join(" ", points);
            }

            SetExpanded(true);
            await Task.Delay(800);
            var home = (ScrollViewer)HomeItems.Parent;
            log.Add($"home scrollable={home.ScrollableHeight:0}");
            Wheel(HomeItems, -120);
            log.Add("home one notch down: " + await Trace(home, 480) + $"  busy={SmoothScroll.Busy}");
            Wheel(HomeItems, -120); Wheel(HomeItems, -120); Wheel(HomeItems, -120);
            log.Add("home three more: " + await Trace(home, 600) + $"  end={home.ScrollableHeight:0}");
            for (int i = 0; i < 20; i++) Wheel(HomeItems, 120);
            log.Add("home 20 up: " + await Trace(home, 600) + $"  busy={SmoothScroll.Busy}");

            var page = new SettingsWindow { ShowActivated = false };
            if (_offscreenTest)
            {
                page.WindowStartupLocation = WindowStartupLocation.Manual;
                page.Left = -20000;
                page.Top = 0;
            }
            page.Show();
            await Task.Delay(900);
            page.Nav.SelectedIndex = 2;
            await Task.Delay(900);
            var inner = (UIElement)page.PageScroll.Content;
            log.Add($"settings scrollable={page.PageScroll.ScrollableHeight:0}");
            Wheel(inner, -120); Wheel(inner, -120);
            log.Add("settings two notches down: " + await Trace(page.PageScroll, 520));
            // a jump from code in the middle of a glide must win
            Wheel(inner, -120);
            await Task.Delay(40);
            page.PageScroll.ScrollToTop();
            log.Add("settings ScrollToTop mid-glide: " + await Trace(page.PageScroll, 320) + $"  busy={SmoothScroll.Busy}");
            page.Close();

            File.WriteAllLines(Path.Combine(dir, "smooth.txt"), log);
            return;
        }

        // PIZTKI_QUICK=scroll: the Features page is taller than the window, so it must scroll
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "scroll")
        {
            var page = new SettingsWindow();
            page.Show();
            await Task.Delay(900);
            page.Nav.SelectedIndex = 2;
            await Task.Delay(1000);
            Snap(page, Path.Combine(dir, "scroll_top.png"));
            page.PageScroll.ScrollToBottom();
            await Task.Delay(500);
            Snap(page, Path.Combine(dir, "scroll_bottom.png"));
            File.WriteAllText(Path.Combine(dir, "scroll.txt"),
                $"scrollable={page.PageScroll.ScrollableHeight:0} offset={page.PageScroll.VerticalOffset:0}");
            page.Close();
            return;
        }

        // PIZTKI_QUICK=beat: record what the beat analysis reads for four seconds, and stop
        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "beat")
        {
            bool started = _beatService.Start();
            await Task.Delay(1500); // let its quiet/loud references settle
            var lines = new List<string> { $"started={started} playing={_media.IsPlaying}", "bass mid high" };
            for (int i = 0; i < 40; i++)
            {
                var (bass, mid, high) = _beatService.Levels;
                lines.Add($"{bass:0.00} {mid:0.00} {high:0.00}");
                await Task.Delay(100);
            }
            File.WriteAllLines(Path.Combine(dir, "beat.txt"), lines);
            return;
        }

        if (Environment.GetEnvironmentVariable("PIZTKI_QUICK") == "1")
        {
            await Task.Delay(1500);
            Shot("song_color_pill");
            SetExpanded(true);
            await Task.Delay(1200);
            Shot("song_color_open");
            string problem = "none";
            try
            {
                // a known warm brown, so the check doesn't depend on which song happens to be playing
                ApplySongColor(Color.FromRgb(0x95, 0x64, 0x48));
            }
            catch (Exception ex)
            {
                problem = ex.GetType().Name + ": " + ex.Message;
            }
            await Task.Delay(1300);
            Shot("song_color_reapplied");
            SetExpanded(false);
            await Task.Delay(900);
            Shot("song_color_brown_pill");
            SetExpanded(true);
            await Task.Delay(700);
            File.WriteAllText(Path.Combine(dir, "song_color_problem.txt"),
                $"{problem} | tintOpacity={Tint.Opacity} bottom={TintBottom.Color} accentNow={_accentNow} override={_accentOverride}");
            // weather: typing part of a city should list places to pick from
            var weatherSettings = new SettingsWindow();
            weatherSettings.Show();
            await Task.Delay(900);
            weatherSettings.Nav.SelectedIndex = 8;
            await Task.Delay(800);
            weatherSettings.CityBox.Text = "tel av";
            await Task.Delay(2600);
            Snap(weatherSettings, Path.Combine(dir, $"{++n:00}_weather_search.png"));
            weatherSettings.Close();

            File.WriteAllText(Path.Combine(dir, "song_color.txt"),
                $"title={_media.Title} art={(_media.Art == null ? "none" : "yes")} artColor={_media.ArtColor?.ToString() ?? "null"} " +
                $"applied={_songColor?.ToString() ?? "null"} setting={_s.SongColors} session={_media.HasSession}");
            return;
        }

        // the pill should grow to fit a whole line, and only trim when a line is absurdly long
        Peek("Said, \"Come on Superman, say your stupid line\"", celebrate: false);
        await Task.Delay(700);
        Shot("pill_fits_long_line");
        Peek("Hi", celebrate: false);
        await Task.Delay(700);
        Shot("pill_fits_short_line");
        Peek("This line is far too long to ever fit on one row of the notch, so it has to stop at the widest size and trim the rest away", celebrate: false);
        await Task.Delay(700);
        Shot("pill_max_width");
        _peekUntil = DateTime.MinValue;
        UpdateMini();
        await Task.Delay(600);

        // volume popup in the pill (shown directly: changing the real volume would be rude)
        ShowVolume(0.62, false);
        await Task.Delay(500);
        Shot("volume_popup");
        ShowVolume(0, true);
        await Task.Delay(400);
        Shot("volume_muted");
        await Task.Delay(1900);
        Shot("volume_gone");

        SetExpanded(true);
        await Task.Delay(900);
        Shot("music_seek_lyrics");

        // timers
        TabFocus.IsChecked = true;
        await Task.Delay(600);
        PomoStart_Click(this, click);
        await Task.Delay(2300);
        Shot("focus_running");
        ModeTimer.IsChecked = true;
        await Task.Delay(500);
        QuickAdd_Click(new Button { Tag = "5" }, click);
        PomoStart_Click(this, click);
        await Task.Delay(2300);
        Shot("countdown_running");
        ModeStopwatch.IsChecked = true;
        await Task.Delay(400);
        PomoStart_Click(this, click);
        await Task.Delay(1350);
        Shot("stopwatch_running");
        PomoReset_Click(this, click);
        ModeTimer.IsChecked = true;
        PomoReset_Click(this, click);
        ModeFocus.IsChecked = true;
        await Task.Delay(400);

        // to-do with XP
        TabTodo.IsChecked = true;
        await Task.Delay(500);
        AddTodo("Buy milk");
        await Task.Delay(450);
        AddTodo("Finish Piztki");
        await Task.Delay(500);
        _s.Todos[0].Done = true;
        CompleteTodo(_s.Todos[0]);
        await Task.Delay(900);
        Shot("todo_done_xp");

        // water
        TabWater.IsChecked = true;
        await Task.Delay(500);
        WaterAdd_Click(this, click);
        await Task.Delay(700);
        Shot("water");

        // shelf
        string note = Path.Combine(dir, "shelf note.txt");
        File.WriteAllText(note, "hello");
        TabShelf.IsChecked = true;
        await Task.Delay(600);
        Shot("shelf_empty");
        AddToShelf(new[] { note, Environment.ProcessPath ?? note, dir, @"C:\missing\gone.pdf" });
        await Task.Delay(900);
        Shot("shelf_files");

        // clipboard
        TabClip.IsChecked = true;
        await Task.Delay(600);
        Shot("clipboard_empty");
        AddClip("Hello from Piztki");
        AddClip("A second thing I copied,\r\nwith two lines that should show as one");
        AddClip("https://example.com/a/very/long/link/that/needs/to/be/trimmed/because/it/does/not/fit/in/the/row");
        await Task.Delay(700);
        Shot("clipboard_items");

        // system
        TabSystem.IsChecked = true;
        await Task.Delay(2600);
        Shot("system");

        // weather (a real lookup, so this needs a connection)
        _s.WeatherCity = "Tel Aviv";
        _s.ShowWeather = true;
        await Task.Delay(3500);
        Shot("weather_under_clock");

        // level up
        AddXp(60);
        await Task.Delay(900);
        Shot("level_up_open");
        SetExpanded(false);
        await Task.Delay(700);
        Shot("level_up_peek");

        // fewer tabs
        SetExpanded(true);
        _s.ShowShelf = false;
        _s.ShowClipboard = false;
        _s.ShowSystem = false;
        await Task.Delay(900);
        Shot("three_tabs_off");
        _s.ShowShelf = true;
        _s.ShowClipboard = true;
        _s.ShowSystem = true;
        SetExpanded(false);
        await Task.Delay(500);

        // every texture, hat, and mood side by side
        var gallery = BuildGallery();
        gallery.Show();
        await Task.Delay(900);
        Snap(gallery, Path.Combine(dir, $"{++n:00}_mascot_gallery.png"));
        gallery.Close();

        // skin applied to the live notch
        _s.Texture = "Honeycomb";
        _s.Hat = "Crown";
        _s.Accent = "#FAC775";
        SetExpanded(true);
        TabMusic.IsChecked = true;
        await Task.Delay(1000);
        Shot("skin_on_notch");
        SetExpanded(false);
        await Task.Delay(700);
        Shot("skin_on_pill");

        // sound level meter that drives the music bars: record what it reads for two seconds
        var readings = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            bool ok = _volume.TryReadPeak(out double peak);
            readings.Add(ok ? peak.ToString("0.000") : "unavailable");
            await Task.Delay(100);
        }
        File.WriteAllText(Path.Combine(dir, "sound_levels.txt"),
            $"media playing: {_media.IsPlaying}\r\n" + string.Join(" ", readings));

        // tray menu
        var tray = new TrayMenu();
        tray.Show();
        await Task.Delay(900);
        Snap(tray, Path.Combine(dir, $"{++n:00}_tray_menu.png"));
        tray.Close();

        var settings = new SettingsWindow();
        settings.Show();
        await Task.Delay(1100);
        string[] pages = { "general", "notch", "features", "music", "mascot", "style", "timers", "water", "weather", "calendar", "shelf", "about" };
        for (int i = 0; i < pages.Length; i++)
        {
            settings.Nav.SelectedIndex = i;
            await Task.Delay(1000);
            Snap(settings, Path.Combine(dir, $"{++n:00}_settings_{pages[i]}.png"));
        }
        settings.Close();
    }

    static Window BuildGallery()
    {
        var rows = new StackPanel { Margin = new Thickness(16) };

        void Row(string title, IEnumerable<(string label, Action<Mascot> setup)> items)
        {
            rows.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(0, 6, 0, 2) });
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, setup) in items)
            {
                var mascot = new Mascot { Width = 120, Height = 96, Accent = App.AccentBrush };
                setup(mascot);
                var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                cell.Children.Add(mascot);
                cell.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
                line.Children.Add(cell);
            }
            rows.Children.Add(line);
        }

        Row("Textures", new[] { "Stars", "Carbon", "Waves", "Honeycomb", "Plain" }
            .Select(t => (t, (Action<Mascot>)(m => m.Texture = t))));
        Row("Hats", new[] { "None", "Party", "Crown", "Beanie", "Bow" }
            .Select(h => (h, (Action<Mascot>)(m => m.Hat = h))));
        Row("Moods", new[] { MascotMood.Normal, MascotMood.Happy, MascotMood.Sleepy, MascotMood.Sad }
            .Select(mood => (mood.ToString(), (Action<Mascot>)(m => m.Mood = mood))));

        return new Window
        {
            Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0D)), Child = rows },
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -4000,
            Top = 0,
        };
    }

    /// <summary>Renders a window's own visuals to a PNG over a mid-gray backdrop.</summary>
    /// <summary>One frame of a clip: the window drawn over a soft night-sky background.</summary>
    static BitmapSource FilmFrame(Window window)
    {
        int w = (int)window.ActualWidth, h = (int)window.ActualHeight;
        var content = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        content.Render((Visual)window.Content);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1B, 0x1F, 0x3B), Color.FromRgb(0x3A, 0x2A, 0x5E), 55), null, new Rect(0, 0, w, h));
            dc.DrawImage(content, new Rect(0, 0, w, h));
        }
        var final = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        final.Render(visual);
        final.Freeze();
        return final;
    }

    static void Snap(Window window, string path)
    {
        int w = (int)window.ActualWidth, h = (int)window.ActualHeight;
        var content = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        content.Render((Visual)window.Content);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4A)), null, new Rect(0, 0, w, h));
            dc.DrawImage(content, new Rect(0, 0, w, h));
        }

        var final = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        final.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
