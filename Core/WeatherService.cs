using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Piztki.Core;

/// <summary>
/// Current temperature for a city the user typed in, from Open-Meteo (free, no account).
/// Only the city name and its coordinates are sent, and only while the weather setting is on.
/// </summary>
public sealed class WeatherService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    DateTime _last = DateTime.MinValue;
    bool _busy;

    /// <summary>Short text for the notch, like "23° Clear". Empty when off or unavailable.</summary>
    public string Text { get; private set; } = "";

    /// <summary>A sentence for the settings page explaining what is (or isn't) showing.</summary>
    public string Status { get; private set; } = "";

    public event Action? Changed;

    /// <summary>A place the user can pick from the suggestions while typing a city.</summary>
    public sealed record Place(string Name, string Detail, double Latitude, double Longitude)
    {
        public string Display => Detail.Length > 0 ? $"{Name}, {Detail}" : Name;
    }

    /// <summary>
    /// Guesses the user's city from their internet connection, using GeoJS (free, no account).
    /// Accurate to the city, which is all a forecast needs. Runs only when the user asks for it.
    /// Null when it can't tell (offline, or the connection doesn't map to a place).
    /// </summary>
    public static async Task<Place?> LocateAsync()
    {
        try
        {
            string json = await Http.GetStringAsync("https://get.geojs.io/v1/ip/geo.json");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            // GeoJS sends the coordinates as text
            if (!double.TryParse(Text("latitude"), NumberStyles.Float, Inv, out double lat) ||
                !double.TryParse(Text("longitude"), NumberStyles.Float, Inv, out double lon))
                return null;

            string city = Text("city"), region = Text("region"), country = Text("country");
            if (city.Length == 0) city = region; // some connections only resolve to a region
            if (city.Length == 0) return null;

            string detail = string.Join(", ", new[] { region, country }.Where(p => p.Length > 0 && p != city));
            return new Place(city, detail, lat, lon);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Up to five places matching what the user has typed so far. Empty on no match or no connection.</summary>
    public static async Task<List<Place>> SearchAsync(string query)
    {
        var places = new List<Place>();
        query = query.Trim();
        if (query.Length < 2) return places;

        try
        {
            string json = await Http.GetStringAsync(
                "https://geocoding-api.open-meteo.com/v1/search?count=5&language=en&name=" + Uri.EscapeDataString(query));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return places;

            foreach (var r in results.EnumerateArray())
            {
                string name = r.GetProperty("name").GetString() ?? "";
                string region = r.TryGetProperty("admin1", out var a) ? a.GetString() ?? "" : "";
                string country = r.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
                string detail = string.Join(", ", new[] { region, country }.Where(p => p.Length > 0 && p != name));
                places.Add(new Place(name, detail, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
            }
        }
        catch
        {
            // offline: the settings page says so
        }
        return places;
    }

    public async Task RefreshAsync(AppSettings s, bool force = false)
    {
        string city = s.WeatherCity.Trim();
        if (!s.ShowWeather || city.Length == 0)
        {
            Text = "";
            Status = s.ShowWeather ? "Type a city to see its weather." : "";
            Changed?.Invoke();
            return;
        }

        if (_busy || (!force && DateTime.Now - _last < TimeSpan.FromMinutes(30))) return;
        _busy = true;
        _last = DateTime.Now;

        try
        {
            if (!string.Equals(s.WeatherPlace, city, StringComparison.OrdinalIgnoreCase))
            {
                string geo = await Http.GetStringAsync(
                    "https://geocoding-api.open-meteo.com/v1/search?count=1&name=" + Uri.EscapeDataString(city));
                using var geoDoc = JsonDocument.Parse(geo);
                if (!geoDoc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                {
                    Text = "";
                    Status = $"Couldn't find \"{city}\". Check the spelling.";
                    return;
                }
                s.WeatherLat = results[0].GetProperty("latitude").GetDouble();
                s.WeatherLon = results[0].GetProperty("longitude").GetDouble();
                s.WeatherPlace = city;
            }

            string url = "https://api.open-meteo.com/v1/forecast?current=temperature_2m,weather_code" +
                         "&latitude=" + s.WeatherLat.ToString("0.###", Inv) +
                         "&longitude=" + s.WeatherLon.ToString("0.###", Inv) +
                         (s.Fahrenheit ? "&temperature_unit=fahrenheit" : "");
            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement.GetProperty("current");
            double temp = current.GetProperty("temperature_2m").GetDouble();
            int code = current.GetProperty("weather_code").GetInt32();

            Text = $"{Math.Round(temp)}° {Describe(code)}";
            Status = $"Showing {city}: {Text}";
        }
        catch
        {
            Text = "";
            Status = "Couldn't reach the weather service. Piztki will try again later.";
            _last = DateTime.Now - TimeSpan.FromMinutes(25); // retry in about five minutes
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }

    // WMO weather codes, grouped into one word each
    static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 or 2 => "Fair",
        3 => "Cloudy",
        45 or 48 => "Fog",
        >= 51 and <= 67 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow",
        >= 95 => "Storm",
        _ => "",
    };
}
