using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Piztki.Core;

/// <summary>How one Counter-Strike 2 match ended, for the player at this PC.</summary>
public sealed record Cs2Match(string Map, string Mode, int MyScore, int TheirScore, int Kills, int Deaths, int Assists, int Mvps)
{
    public bool Won => MyScore > TheirScore;

    /// <summary>"Won 13-9", "Lost 9-13", "Draw 12-12".</summary>
    public string Headline => $"{(MyScore > TheirScore ? "Won" : MyScore < TheirScore ? "Lost" : "Draw")} {MyScore}-{TheirScore}";

    /// <summary>"Mirage · 18 K · 12 D · 4 A".</summary>
    public string Detail => $"{Map} · {Kills} K · {Deaths} D · {Assists} A";

    public string Line => $"Counter-Strike 2    {Headline}    {Map}    {Kills} K  {Deaths} D  {Assists} A" + (Mvps > 0 ? $"    {Mvps} MVP" : "");
}

/// <summary>
/// Counter-Strike's "Game State Integration": Valve's own, documented way for the game to tell
/// another program how a match is going. A small settings file in the game's cfg folder asks the
/// game to send its state to an address on this PC; this class is what listens there. Nothing
/// reads the game's memory, and nothing leaves the PC.
/// </summary>
public sealed class Cs2Gsi : IDisposable
{
    public const int Port = 47311;
    const string CfgName = "gamestate_integration_piztki.cfg";

    TcpListener? _listener;
    CancellationTokenSource? _stop;
    string _token = "";

    // what the game last said about the player at this PC
    string _myTeam = "";
    int _kills, _deaths, _assists, _mvps;
    string _phase = "";
    bool _told;

    public event Action<Cs2Match>? MatchEnded;

    /// <summary>Counter-Strike 2's cfg folder on this PC, or null when the game isn't installed through Steam.</summary>
    public static string? FindCfgFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is not string steam) return null;
            steam = steam.Replace('/', '\\');
            var libraries = new List<string> { steam };
            string index = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(index))
                foreach (Match m in Regex.Matches(File.ReadAllText(index), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string cfg = Path.Combine(library, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "cfg");
                if (Directory.Exists(cfg)) return cfg;
            }
        }
        catch
        {
            // no Steam, no Counter-Strike
        }
        return null;
    }

    /// <summary>The settings file's text: where to send, how often, and which parts of the state.</summary>
    public static string CfgText(string token, int port = Port) =>
        "\"Piztki\"\n{\n" +
        $"  \"uri\" \"http://127.0.0.1:{port}/\"\n" +
        "  \"timeout\" \"5.0\"\n  \"buffer\" \"0.5\"\n  \"throttle\" \"1.0\"\n  \"heartbeat\" \"30.0\"\n" +
        $"  \"auth\"\n  {{\n    \"token\" \"{token}\"\n  }}\n" +
        "  \"data\"\n  {\n    \"provider\" \"1\"\n    \"map\" \"1\"\n    \"player_id\" \"1\"\n    \"player_state\" \"1\"\n    \"player_match_stats\" \"1\"\n  }\n}\n";

    /// <summary>Puts the settings file into the game's cfg folder. False when the game wasn't found or the folder can't be written.</summary>
    public static bool Install(string token, string? folder = null)
    {
        try
        {
            folder ??= FindCfgFolder();
            if (folder == null) return false;
            File.WriteAllText(Path.Combine(folder, CfgName), CfgText(token));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Remove(string? folder = null)
    {
        try
        {
            folder ??= FindCfgFolder();
            if (folder == null) return;
            string file = Path.Combine(folder, CfgName);
            if (File.Exists(file)) File.Delete(file);
        }
        catch
        {
            // nothing to tidy
        }
    }

    /// <summary>Starts listening on this PC only. The token is the one written into the settings file: anything else is ignored.</summary>
    public bool Start(string token, int port = Port)
    {
        Stop();
        try
        {
            _token = token;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _stop = new CancellationTokenSource();
            _ = Listen(_listener, _stop.Token);
            return true;
        }
        catch
        {
            _listener = null; // the port is taken: the summary just won't appear
            return false;
        }
    }

    public void Stop()
    {
        _stop?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    async Task Listen(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop); }
            catch { return; }
            _ = Serve(client, stop);
        }
    }

    /// <summary>One small HTTP request from the game: read its JSON, answer "OK".</summary>
    async Task Serve(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 4000;
                var stream = client.GetStream();
                var data = new MemoryStream();
                var buffer = new byte[16384];
                int headerEnd = -1, length = 0;
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
                limit.CancelAfter(4000);
                while (data.Length < 2_000_000)
                {
                    int got = await stream.ReadAsync(buffer, limit.Token);
                    if (got <= 0) break;
                    data.Write(buffer, 0, got);
                    if (headerEnd < 0)
                    {
                        string soFar = Encoding.ASCII.GetString(data.GetBuffer(), 0, (int)Math.Min(data.Length, 8192));
                        int end = soFar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (end < 0) continue;
                        headerEnd = end + 4;
                        var size = Regex.Match(soFar[..end], @"(?im)^Content-Length:\s*(\d+)");
                        length = size.Success ? int.Parse(size.Groups[1].Value) : 0;
                    }
                    if (data.Length >= headerEnd + length) break;
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), limit.Token);
                if (headerEnd > 0 && length > 0 && data.Length >= headerEnd + length)
                    Read(Encoding.UTF8.GetString(data.GetBuffer(), headerEnd, length));
            }
            catch
            {
                // a half-sent message: the game sends another one in a moment
            }
        }
    }

    static string Text(JsonElement from, string name) =>
        from.ValueKind == JsonValueKind.Object && from.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int Number(JsonElement from, string name) =>
        from.ValueKind == JsonValueKind.Object && from.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;

    static JsonElement Part(JsonElement from, string name) =>
        from.ValueKind == JsonValueKind.Object && from.TryGetProperty(name, out var v) ? v : default;

    /// <summary>Takes one message from the game. Public so a test can feed it the same text the game would send.</summary>
    public void Read(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (_token.Length > 0 && Text(Part(root, "auth"), "token") != _token) return;

            // while dead the game reports whoever is being watched: only keep numbers that are the player's own
            string me = Text(Part(root, "provider"), "steamid");
            var player = Part(root, "player");
            if (me.Length > 0 && Text(player, "steamid") == me)
            {
                string team = Text(player, "team");
                if (team.Length > 0) _myTeam = team;
                var stats = Part(player, "match_stats");
                if (stats.ValueKind == JsonValueKind.Object)
                {
                    _kills = Number(stats, "kills");
                    _deaths = Number(stats, "deaths");
                    _assists = Number(stats, "assists");
                    _mvps = Number(stats, "mvps");
                }
            }

            var map = Part(root, "map");
            if (map.ValueKind != JsonValueKind.Object)
            {
                _phase = ""; // back in the menu
                _told = false;
                return;
            }
            string phase = Text(map, "phase");
            if (phase != "gameover") _told = false;
            if (phase == "gameover" && _phase != "gameover" && !_told && _myTeam.Length > 0)
            {
                _told = true;
                int ct = Number(Part(map, "team_ct"), "score"), t = Number(Part(map, "team_t"), "score");
                bool amCt = _myTeam == "CT";
                MatchEnded?.Invoke(new Cs2Match(MapName(Text(map, "name")), Text(map, "mode"),
                    amCt ? ct : t, amCt ? t : ct, _kills, _deaths, _assists, _mvps));
            }
            _phase = phase;
        }
    }

    /// <summary>"de_mirage" → "Mirage", "de_dust2" → "Dust 2".</summary>
    public static string MapName(string raw)
    {
        string name = raw;
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        name = Regex.Replace(name, "^(de|cs|ar|dm|gd)_", "");
        name = Regex.Replace(name.Replace('_', ' '), @"(?<=[a-z])(?=\d)", " ");
        return name.Length == 0 ? "Unknown map" : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
