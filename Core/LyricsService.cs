using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Piztki.Core;

/// <summary>
/// Fetches time-synced lyrics for the current song from LRCLIB (a free, open lyrics database).
/// Only the song title and artist are sent, and only while the lyrics setting is on.
/// </summary>
public sealed class LyricsService
{
    static readonly HttpClient Http = CreateClient();
    static readonly Regex LrcLine = new(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$", RegexOptions.Compiled);

    readonly Dictionary<string, (List<(TimeSpan time, string text)> timed, List<string> plain)> _cache = new();
    List<(TimeSpan time, string text)> _lines = new();
    List<string> _plain = new();
    string _key = "";

    public bool HasLyrics => _lines.Count > 0;

    /// <summary>
    /// The words with no timing at all, for songs the database has text for but nobody has timed.
    /// Only filled when there are no timed lyrics.
    /// </summary>
    public IReadOnlyList<string> PlainLines => _plain;
    public bool HasPlain => _plain.Count > 0;

    /// <summary>Every line of the current song with the moment it starts, in order.</summary>
    public IReadOnlyList<(TimeSpan time, string text)> Lines => _lines;

    /// <summary>Which line is being sung at this position, or -1 before the first one.</summary>
    public int IndexAt(TimeSpan position)
    {
        int index = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].time > position) break;
            index = i;
        }
        return index;
    }

    /// <summary>True while a lookup is on its way, so "not available" isn't shown before we know.</summary>
    public bool Loading { get; private set; }
    public event Action? Changed;

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Piztki/1.0");
        return http;
    }

    public void Clear()
    {
        if (_key.Length == 0 && _lines.Count == 0 && _plain.Count == 0) return;
        _key = "";
        _lines = new();
        _plain = new();
        Loading = false;
        Changed?.Invoke();
    }

    public async void Load(string title, string artist, TimeSpan duration)
    {
        string key = title + "\n" + artist;
        if (key == _key) return;
        _key = key;
        _lines = new();
        _plain = new();
        Loading = title.Length > 0 && !_cache.ContainsKey(key);
        Changed?.Invoke();
        if (title.Length == 0) return;

        if (_cache.TryGetValue(key, out var cached))
        {
            _lines = cached.timed;
            _plain = cached.plain;
            Changed?.Invoke();
            return;
        }

        var found = new List<(TimeSpan, string)>();
        var plain = new List<string>();
        try
        {
            string url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(CleanTitle(title)) +
                         "&artist_name=" + Uri.EscapeDataString(artist);
            string json = await Http.GetStringAsync(url);
            if (key != _key) return; // the song changed while we were waiting

            using var doc = JsonDocument.Parse(json);
            string? best = null;
            string? untimed = null;
            double bestGap = double.MaxValue;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                // remember the first plain text too, in case no version of the song has been timed
                if (untimed == null && item.TryGetProperty("plainLyrics", out var text) && text.ValueKind == JsonValueKind.String)
                    untimed = text.GetString();

                if (!item.TryGetProperty("syncedLyrics", out var synced) || synced.ValueKind != JsonValueKind.String) continue;
                double length = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
                // several versions of a song can exist: take the one closest in length to what's playing
                double gap = duration > TimeSpan.Zero && length > 0 ? Math.Abs(length - duration.TotalSeconds) : 1000;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = synced.GetString();
                }
            }
            if (best != null) found = Parse(best);
            if (found.Count == 0 && !string.IsNullOrWhiteSpace(untimed))
                plain = untimed.Split('\n').Select(l => l.Trim()).ToList();
        }
        catch
        {
            // offline or the service is down: show "not available" now, and try again next time this song plays
            if (key == _key)
            {
                Loading = false;
                Changed?.Invoke();
            }
            return;
        }

        if (key != _key) return;
        _cache[key] = (found, plain);
        _lines = found;
        _plain = plain;
        Loading = false;
        Changed?.Invoke();
    }

    /// <summary>Video-style titles ("Song (Official Video)") match better without the suffix.</summary>
    static string CleanTitle(string title)
    {
        string cleaned = Regex.Replace(title, @"\s*[\(\[][^\)\]]*(official|video|audio|lyrics?|remaster(ed)?|visuali[sz]er|hd|4k)[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
        return cleaned.Trim().Length > 0 ? cleaned.Trim() : title;
    }

    static List<(TimeSpan, string)> Parse(string lrc)
    {
        var lines = new List<(TimeSpan, string)>();
        foreach (var raw in lrc.Split('\n'))
        {
            var m = LrcLine.Match(raw.Trim());
            if (!m.Success) continue;
            double seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
                             double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            lines.Add((TimeSpan.FromSeconds(seconds), m.Groups[3].Value.Trim()));
        }
        lines.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return lines;
    }

    /// <summary>The line being sung at this moment, or "" during an instrumental gap.</summary>
    public string LineAt(TimeSpan position)
    {
        string current = "";
        foreach (var (time, text) in _lines)
        {
            if (time > position) break;
            current = text;
        }
        return current;
    }
}
