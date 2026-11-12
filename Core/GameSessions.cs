using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Piztki.Core;

/// <summary>One sitting with a game: from when it was first seen filling the screen until it closed.</summary>
public sealed record GameSession(string Name, string Exe, DateTime Started, DateTime Ended)
{
    public TimeSpan Length => Ended - Started;

    /// <summary>"34 min" or "1 h 12 min".</summary>
    public string LengthText
    {
        get
        {
            int minutes = Math.Max(1, (int)Math.Round(Length.TotalMinutes));
            return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
        }
    }
}

/// <summary>
/// Notices any game by the way it behaves: a window that fills the screen, from a program that
/// isn't a browser or a video player. Says which game it was and for how long once it closes.
/// Nothing about the game is read except its name and whether it is still running.
/// </summary>
public sealed class GameWatch
{
    sealed class Seen
    {
        public string Name = "", Exe = "";
        public DateTime First;
        public double FullscreenSeconds;
        public bool IsGame;
        public DateTime LastPoll;
    }

    const double SecondsToCount = 120; // an unknown program must fill the screen this long before it counts as a game

    readonly Dictionary<int, Seen> _seen = new();

    /// <summary>Programs that fill the screen without being games (browsers, players, slides).</summary>
    public HashSet<string> NotGames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Games that already get a summary of their own (their program's name, without ".exe").</summary>
    public HashSet<string> HandledElsewhere { get; } = new(StringComparer.OrdinalIgnoreCase);

    public event Action<GameSession>? Ended;

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    const uint QueryLimited = 0x1000; // works even for games an anti-cheat protects
    const uint StillActive = 259;

    /// <summary>Call every couple of seconds. "self" is the notch's own window, which never counts.</summary>
    public void Poll(IntPtr self)
    {
        var now = DateTime.Now;

        // has a game we were following closed?
        foreach (var (pid, seen) in _seen.ToArray())
        {
            if (Running(pid)) continue;
            _seen.Remove(pid);
            if (seen.IsGame) Ended?.Invoke(new GameSession(seen.Name, seen.Exe, seen.First, now));
        }

        IntPtr window = Native.FullscreenForeground(self);
        if (window == IntPtr.Zero) return;
        GetWindowThreadProcessId(window, out uint id);
        if (id == 0 || id == (uint)Environment.ProcessId) return;

        if (!_seen.TryGetValue((int)id, out var game))
        {
            string exe = PathOf(id);
            string program = Path.GetFileNameWithoutExtension(exe);
            if (program.Length == 0 || NotGames.Contains(program) || HandledElsewhere.Contains(program) || IsWindowsPart(exe)) return;

            var title = new StringBuilder(200);
            GetWindowText(window, title, title.Capacity);
            game = new Seen { Exe = exe, Name = NameFor(exe, title.ToString()), First = now, LastPoll = now, IsGame = InGameLibrary(exe) };
            _seen[(int)id] = game;
            return;
        }

        game.FullscreenSeconds += Math.Min(10, (now - game.LastPoll).TotalSeconds);
        game.LastPoll = now;
        if (!game.IsGame && game.FullscreenSeconds >= SecondsToCount) game.IsGame = true;
    }

    static bool Running(int pid)
    {
        IntPtr handle = OpenProcess(QueryLimited, false, (uint)pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return GetExitCodeProcess(handle, out uint code) && code == StillActive;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static string PathOf(uint pid)
    {
        IntPtr handle = OpenProcess(QueryLimited, false, pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : "";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static bool IsWindowsPart(string exe) =>
        exe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);

    static readonly string[] Libraries =
    {
        @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\Rockstar Games\", @"\XboxGames\", @"\Ubisoft Game Launcher\games\",
        @"\Origin Games\", @"\EA Games\", @"\GOG Games\", @"\GOG Galaxy\Games\", @"\Battle.net\", @"\.minecraft\", @"\Hytale\",
    };

    /// <summary>The program lives where a game store keeps its games: no need to wait and see.</summary>
    public static bool InGameLibrary(string exe) =>
        Libraries.Any(folder => exe.Contains(folder, StringComparison.OrdinalIgnoreCase));

    static readonly string[] EngineNames =
    {
        "unreal engine", "unrealengine", "ue4game", "ue4", "ue5", "unity", "unity player", "unityplayer", "godot engine",
        "java(tm) platform se binary", "openjdk platform binary", "electron",
    };

    /// <summary>The game's name as a person would say it, from the best source there is.</summary>
    public static string NameFor(string exe, string windowTitle)
    {
        // Steam knows the proper name of everything it installed
        if (SteamName(exe) is { Length: > 0 } steam) return steam;

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            foreach (string? candidate in new[] { info.ProductName, info.FileDescription })
            {
                string name = (candidate ?? "").Trim();
                if (name.Length > 1 && name.Length <= 48 && !EngineNames.Contains(name.ToLowerInvariant())) return name;
            }
        }
        catch
        {
            // a protected program's file can't always be read
        }

        // "Minecraft 1.21.4 - Singleplayer" -> "Minecraft"
        string title = Regex.Replace(windowTitle.Trim(), @"\s+[-–|:]\s+.*$", "");
        title = Regex.Replace(title, @"\s+v?\d[\d.]*\*?$", "").Trim('*', ' ');
        if (title.Length > 1 && title.Length <= 40) return title;
        if (SteamFolder(exe) is { Length: > 0 } folder) return folder.Replace('_', ' ');

        string file = Path.GetFileNameWithoutExtension(exe);
        file = Regex.Replace(file, @"(-Win64-Shipping|_x64|-x64|64|Launcher)$", "", RegexOptions.IgnoreCase);
        return file.Length > 0 ? file : "A game";
    }

    /// <summary>The folder Steam installed the game into, when the program is under ...\steamapps\common\.</summary>
    static string? SteamFolder(string exe)
    {
        const string marker = @"\steamapps\common\";
        int at = exe.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        string rest = exe[(at + marker.Length)..];
        int slash = rest.IndexOf(System.IO.Path.DirectorySeparatorChar);
        return slash > 0 ? rest[..slash] : null;
    }

    /// <summary>For a program under ...\steamapps\common\Folder\: the name in Steam's own record of that folder.</summary>
    static string? SteamName(string exe)
    {
        try
        {
            const string marker = @"\steamapps\common\";
            int at = exe.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            string rest = exe[(at + marker.Length)..];
            int slash = rest.IndexOf('\\');
            if (slash <= 0) return null;
            string folder = rest[..slash];
            string steamapps = exe[..(at + @"\steamapps".Length)];
            foreach (string manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(manifest);
                var dir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]*)\"");
                if (!dir.Success || !string.Equals(dir.Groups[1].Value, folder, StringComparison.OrdinalIgnoreCase)) continue;
                var name = Regex.Match(text, "\"name\"\\s+\"([^\"]*)\"");
                return name.Success ? name.Groups[1].Value.Trim() : null;
            }
        }
        catch
        {
            // Steam's records are a nicety
        }
        return null;
    }
}
