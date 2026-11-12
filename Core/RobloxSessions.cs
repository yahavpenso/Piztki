using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Piztki.Core;

/// <summary>One visit to a Roblox game, from joining it to being back in the Roblox menu.</summary>
public sealed record RobloxSession(
    long UniverseId, long PlaceId, DateTime Joined, DateTime Left,
    string JobId = "", string ServerIp = "", double JoinSeconds = 0, int Teleports = 0)
{
    public TimeSpan Length => Left - Joined;

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

/// <summary>What Roblox's website says about a game.</summary>
public sealed record RobloxGame(string Name, string Creator, long Playing, long Visits, int MaxPlayers, DateTime? Updated)
{
    /// <summary>"2 days ago", "today".</summary>
    public string UpdatedText
    {
        get
        {
            if (Updated is not { } at) return "";
            int days = (int)(DateTime.UtcNow - at.ToUniversalTime()).TotalDays;
            return days <= 0 ? "updated today" : days == 1 ? "updated yesterday" : days < 60 ? $"updated {days} days ago" : $"updated {days / 30} months ago";
        }
    }
}

/// <summary>One server of a game, from Roblox's public server list. The ping is the average of everyone on it.</summary>
public sealed record RobloxServer(int Playing, int MaxPlayers, int Ping, double Fps);

/// <summary>
/// Follows Roblox's own log file (AppData\Local\Roblox\logs) to notice when the player joins and
/// leaves a game. Only that file is read: nothing touches the running game. Poll it every few seconds.
/// </summary>
public sealed class RobloxWatch
{
    string _file = "";
    long _read;
    string _rest = "";

    // the visit going on right now
    long _place, _universe;
    string _job = "", _ip = "";
    double _joinSeconds;
    int _teleports;
    DateTime _joined;
    bool _inGame, _awaitingUniverse, _unannounced;
    DateTime _pendingJoin;
    long _pendingPlace;
    string _pendingJob = "";

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

    /// <summary>The player is in a game now (said once per visit, when the server's address is known).</summary>
    public event Action<RobloxSession>? Joined;

    /// <summary>The newest visit that ended since the last poll.</summary>
    public event Action<RobloxSession>? Finished;

    /// <summary>For tests: follow this file instead of the newest one in Roblox's folder.</summary>
    public string? FileOverride { get; set; }

    public void Poll()
    {
        try
        {
            string? path = FileOverride ?? Newest();
            if (path == null) return;
            if (path != _file)
            {
                _file = path;
                _read = 0;
                _rest = "";
                _inGame = _awaitingUniverse = _unannounced = false;
            }

            RobloxSession? ended = null;
            bool joined = false;
            var info = new FileInfo(path);
            if (info.Length < _read) _read = 0; // started over
            if (info.Length > _read)
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                file.Seek(_read, SeekOrigin.Begin);
                var bytes = new byte[Math.Min(info.Length - _read, 8 * 1024 * 1024)];
                int got = file.Read(bytes, 0, bytes.Length);
                _read += got;
                string text = _rest + Encoding.UTF8.GetString(bytes, 0, got);
                int lastBreak = text.LastIndexOf('\n');
                _rest = lastBreak < 0 ? text : text[(lastBreak + 1)..];
                if (_rest.Length > 65536) _rest = ""; // one endless line: not something this reader needs
                if (lastBreak >= 0)
                    foreach (var line in text.AsSpan(0, lastBreak).EnumerateLines())
                    {
                        var done = Follow(line, out bool nowIn);
                        if (done != null)
                        {
                            ended = done;
                            joined = false;
                        }
                        if (nowIn) joined = true;
                    }
            }
            else if (_inGame && (DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds > 20 && !RobloxRunning())
            {
                // the window was simply closed in the middle of a game: no goodbye in the log
                ended = End(info.LastWriteTimeUtc);
            }

            if (ended != null) Finished?.Invoke(ended);
            if (joined && _inGame) Joined?.Invoke(Current(_joined));
        }
        catch
        {
            // a log Roblox is rotating right now: the next poll gets it
        }
    }

    static string? Newest()
    {
        var folder = new DirectoryInfo(Folder);
        if (!folder.Exists) return null;
        return folder.EnumerateFiles("*_Player_*_last.log")
            .Where(f => !f.Name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    static bool RobloxRunning()
    {
        var found = Process.GetProcessesByName("RobloxPlayerBeta");
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    /// <summary>The text after a marker, up to the next of the given end characters.</summary>
    static ReadOnlySpan<char> After(ReadOnlySpan<char> line, string marker, ReadOnlySpan<char> ends)
    {
        int at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return default;
        var rest = line[(at + marker.Length)..];
        int end = rest.IndexOfAny(ends);
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>Reads one log line; returns a visit when that line ends one. "nowIn" = the player has just landed in a game.</summary>
    RobloxSession? Follow(ReadOnlySpan<char> line, out bool nowIn)
    {
        nowIn = false;
        if (line.Length < 30 || line[4] != '-' || line[10] != 'T') return null;
        if (!DateTime.TryParse(line[..24], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return null;

        if (line.IndexOf("! Joining game '", StringComparison.Ordinal) >= 0)
        {
            if (!long.TryParse(After(line, "' place ", " "), out _pendingPlace)) return null;
            _pendingJob = After(line, "! Joining game '", "'").ToString();
            _pendingJoin = at;
            _awaitingUniverse = true;
            return null;
        }

        if (_awaitingUniverse && line.IndexOf("universeid:", StringComparison.Ordinal) >= 0)
        {
            _awaitingUniverse = false;
            if (!long.TryParse(After(line, "universeid:", ", "), out long universe)) return null;
            double.TryParse(After(line, "join_time:", ", "), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds);
            if (_inGame && universe == _universe)
            {
                // a teleport inside the same game: the visit carries on, on another server
                _place = _pendingPlace;
                _job = _pendingJob;
                _teleports++;
                return null;
            }
            var before = _inGame ? End(_pendingJoin) : null;
            _inGame = true;
            _unannounced = true;
            _universe = universe;
            _place = _pendingPlace;
            _job = _pendingJob;
            _joined = _pendingJoin;
            _joinSeconds = seconds;
            _teleports = 0;
            _ip = "";
            return before;
        }

        if (_inGame && line.IndexOf("UDMUX Address = ", StringComparison.Ordinal) >= 0)
        {
            var ip = After(line, "UDMUX Address = ", ", ");
            if (System.Net.IPAddress.TryParse(ip, out _)) _ip = ip.ToString();
            if (_unannounced)
            {
                _unannounced = false;
                nowIn = true;
            }
            return null;
        }

        if (_inGame && line.IndexOf("] leaveUGCGameInternal", StringComparison.Ordinal) >= 0) return End(at);
        return null;
    }

    RobloxSession Current(DateTime left) => new(_universe, _place, _joined, left, _job, _ip, _joinSeconds, _teleports);

    RobloxSession End(DateTime at)
    {
        _inGame = _unannounced = false;
        return Current(at);
    }
}

/// <summary>
/// Looks things up about a Roblox game. What is sent: the game's number to roblox.com, and the
/// game server's address (never the player's) to ipwho.is for the city. Nothing about the player.
/// </summary>
public static class RobloxInfo
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    static readonly Dictionary<long, RobloxGame> Games = new();
    static readonly Dictionary<string, string> Places = new();

    static async Task<JsonDocument?> Get(string url)
    {
        try
        {
            using var answer = await Http.GetAsync(url);
            if (!answer.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        }
        catch
        {
            return null;
        }
    }

    static string Text(JsonElement from, string name) =>
        from.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    static double Number(JsonElement from, string name) =>
        from.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : 0;

    /// <summary>The game's public name alone, or null.</summary>
    public static async Task<string?> NameOf(long universeId) => (await Game(universeId))?.Name;

    public static async Task<RobloxGame?> Game(long universeId)
    {
        lock (Games)
            if (Games.TryGetValue(universeId, out var known)) return known;
        using var json = await Get($"https://games.roblox.com/v1/games?universeIds={universeId}");
        if (json == null || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) return null;
        var g = data[0];
        string name = Text(g, "name");
        if (name.Length == 0 || name == "[TITLE UNAVAILABLE]") return null;
        string creator = g.TryGetProperty("creator", out var c) && c.ValueKind == JsonValueKind.Object ? Text(c, "name") : "";
        DateTime? updated = DateTime.TryParse(Text(g, "updated"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u) ? u : null;
        var game = new RobloxGame(name, creator, (long)Number(g, "playing"), (long)Number(g, "visits"), (int)Number(g, "maxPlayers"), updated);
        lock (Games) Games[universeId] = game;
        return game;
    }

    /// <summary>Finds the player's own server in the game's public server list. Private servers and very busy games may not be found.</summary>
    public static async Task<RobloxServer?> Server(long placeId, string jobId)
    {
        if (jobId.Length == 0) return null;
        string cursor = "";
        for (int page = 0; page < 4; page++)
        {
            using var json = await Get($"https://games.roblox.com/v1/games/{placeId}/servers/Public?limit=100" + (cursor.Length > 0 ? "&cursor=" + Uri.EscapeDataString(cursor) : ""));
            if (json == null || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            foreach (var s in data.EnumerateArray())
                if (string.Equals(Text(s, "id"), jobId, StringComparison.OrdinalIgnoreCase))
                    return new RobloxServer((int)Number(s, "playing"), (int)Number(s, "maxPlayers"), (int)Math.Round(Number(s, "ping")), Number(s, "fps"));
            cursor = Text(json.RootElement, "nextPageCursor");
            if (cursor.Length == 0) return null;
        }
        return null;
    }

    /// <summary>"Frankfurt, Germany" for a game server's address, or null.</summary>
    public static async Task<string?> Where(string serverIp)
    {
        if (!System.Net.IPAddress.TryParse(serverIp, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        var b = ip.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31)) return null; // not a public address
        lock (Places)
            if (Places.TryGetValue(serverIp, out var known)) return known;
        using var json = await Get($"https://ipwho.is/{serverIp}?fields=success,city,country");
        if (json == null) return null;
        string city = Text(json.RootElement, "city"), country = Text(json.RootElement, "country");
        int cut = city.IndexOf(" am ", StringComparison.Ordinal); // "Frankfurt am Main" reads better as "Frankfurt"
        if (cut > 0) city = city[..cut];
        string place = city.Length > 0 && country.Length > 0 ? $"{city}, {country}" : city + country;
        if (place.Length == 0) return null;
        lock (Places) Places[serverIp] = place;
        return place;
    }

    /// <summary>Downloads the game's small picture and returns the file it was saved to, or null.</summary>
    public static async Task<string?> Icon(long universeId)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piztki", "game-icons");
            string file = Path.Combine(folder, universeId + ".png");
            if (File.Exists(file) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalDays < 7) return file;
            using var json = await Get($"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&size=150x150&format=Png&isCircular=false");
            if (json == null || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) return null;
            string url = Text(data[0], "imageUrl");
            if (!url.StartsWith("https://", StringComparison.Ordinal)) return null;
            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length < 100 || bytes.Length > 2_000_000) return null;
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(file, bytes);
            return file;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>12400 → "12.4K", 1200000 → "1.2M".</summary>
    public static string Short(long n) =>
        n >= 1_000_000_000 ? (n / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B"
        : n >= 1_000_000 ? (n / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : n >= 10_000 ? (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : n.ToString("N0", CultureInfo.InvariantCulture);
}
