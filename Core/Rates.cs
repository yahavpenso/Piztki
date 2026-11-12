using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Piztki.Core;

/// <summary>
/// Currency rates for the search box, from the free Frankfurter service (European Central Bank
/// reference rates, updated once a working day). Only asked for when the user types a currency
/// question, and each base currency is remembered for twelve hours.
/// </summary>
public static class Rates
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static readonly Dictionary<string, (DateTime at, Dictionary<string, double> rates)> Cache = new();

    /// <summary>"20 USD = 73.40 ILS", or null when the rate can't be fetched right now.</summary>
    public static async Task<string?> ConvertAsync(double amount, string from, string to)
    {
        if (from == to) return $"{Calc.Show(amount)} {to}";
        var rates = await GetAsync(from);
        if (rates == null || !rates.TryGetValue(to, out double rate)) return null;
        return $"{(amount * rate).ToString("#,0.00", CultureInfo.InvariantCulture)} {to}";
    }

    static async Task<Dictionary<string, double>?> GetAsync(string @base)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(@base, out var hit) && DateTime.Now - hit.at < TimeSpan.FromHours(12)) return hit.rates;
        }

        try
        {
            string json = await Http.GetStringAsync("https://api.frankfurter.dev/v1/latest?base=" + @base);
            using var doc = JsonDocument.Parse(json);
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var rate in doc.RootElement.GetProperty("rates").EnumerateObject())
                rates[rate.Name] = rate.Value.GetDouble();
            lock (Cache) Cache[@base] = (DateTime.Now, rates);
            return rates;
        }
        catch
        {
            return null; // offline, or the service is down
        }
    }
}
