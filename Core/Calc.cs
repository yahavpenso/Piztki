using System.Globalization;
using System.Text.RegularExpressions;

namespace Piztki.Core;

/// <summary>
/// The search box's little brain for numbers: plain sums ("12*(3+4)"), unit conversions
/// ("5 km to miles"), and spotting currency questions ("20 usd to ils") for Rates to answer.
/// </summary>
public static class Calc
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    // ---------- sums ----------

    /// <summary>Works out an expression. False when the text isn't one (or is just a lone number).</summary>
    public static bool TryEvaluate(string text, out double value)
    {
        value = 0;
        text = text.Trim().TrimStart('=').Replace("×", "*").Replace("÷", "/").Replace(",", ".");
        if (text.Length == 0 || text.Length > 80) return false;
        if (!text.Any(char.IsDigit)) return false;
        // a lone number is not a sum, and letters other than the few function names mean it's a search
        if (double.TryParse(text, NumberStyles.Float, En, out _)) return false;

        try
        {
            int at = 0;
            value = Sum(text, ref at);
            Skip(text, ref at);
            return at == text.Length && !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch
        {
            return false;
        }
    }

    public static string Show(double value)
    {
        double rounded = Math.Round(value, 6);
        if (Math.Abs(rounded) >= 1e12 || (rounded != 0 && Math.Abs(rounded) < 1e-6)) return value.ToString("0.####e+0", En);
        return rounded.ToString("#,0.######", En);
    }

    static void Skip(string s, ref int at)
    {
        while (at < s.Length && s[at] == ' ') at++;
    }

    static double Sum(string s, ref int at)
    {
        double left = Product(s, ref at);
        while (true)
        {
            Skip(s, ref at);
            if (at >= s.Length) return left;
            char op = s[at];
            if (op != '+' && op != '-') return left;
            at++;
            double right = Product(s, ref at);
            left = op == '+' ? left + right : left - right;
        }
    }

    static double Product(string s, ref int at)
    {
        double left = Power(s, ref at);
        while (true)
        {
            Skip(s, ref at);
            if (at >= s.Length) return left;
            char op = s[at];
            if (op != '*' && op != '/' && op != '%') return left;
            at++;
            double right = Power(s, ref at);
            left = op == '*' ? left * right : op == '/' ? left / right : left % right;
        }
    }

    static double Power(string s, ref int at)
    {
        double left = Unary(s, ref at);
        Skip(s, ref at);
        if (at < s.Length && s[at] == '^')
        {
            at++;
            return Math.Pow(left, Power(s, ref at)); // right to left, like on paper
        }
        return left;
    }

    static double Unary(string s, ref int at)
    {
        Skip(s, ref at);
        if (at < s.Length && s[at] == '-') { at++; return -Unary(s, ref at); }
        if (at < s.Length && s[at] == '+') { at++; return Unary(s, ref at); }
        return Atom(s, ref at);
    }

    static double Atom(string s, ref int at)
    {
        Skip(s, ref at);
        if (at >= s.Length) throw new FormatException();

        if (s[at] == '(')
        {
            at++;
            double inner = Sum(s, ref at);
            Skip(s, ref at);
            if (at >= s.Length || s[at] != ')') throw new FormatException();
            at++;
            return inner;
        }

        if (char.IsLetter(s[at]))
        {
            int start = at;
            while (at < s.Length && char.IsLetter(s[at])) at++;
            string name = s[start..at].ToLowerInvariant();
            if (name == "pi") return Math.PI;
            double arg = Atom(s, ref at);
            return name switch
            {
                "sqrt" => Math.Sqrt(arg),
                "abs" => Math.Abs(arg),
                "round" => Math.Round(arg),
                "sin" => Math.Sin(arg * Math.PI / 180),
                "cos" => Math.Cos(arg * Math.PI / 180),
                "tan" => Math.Tan(arg * Math.PI / 180),
                "log" => Math.Log10(arg),
                "ln" => Math.Log(arg),
                _ => throw new FormatException(),
            };
        }

        int from = at;
        while (at < s.Length && (char.IsDigit(s[at]) || s[at] == '.')) at++;
        if (from == at) throw new FormatException();
        double number = double.Parse(s[from..at], NumberStyles.Float, En);
        return number;
    }

    // ---------- "<number> <unit> to <unit>" ----------

    static readonly Regex Question = new(@"^\s*(-?[\d.,]+)\s*([^\d\s][^\s]*(?:\s[^\s]+)?)\s+(?:to|in|=|ל)\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Splits "5 km to miles" into 5, "km", "miles". False when the text has another shape.</summary>
    public static bool TryParseQuestion(string text, out double amount, out string from, out string to)
    {
        amount = 0;
        from = to = "";
        var m = Question.Match(text);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, En, out amount)) return false;
        from = m.Groups[2].Value.Trim().ToLowerInvariant();
        to = m.Groups[3].Value.Trim().ToLowerInvariant();
        return from.Length > 0 && to.Length > 0;
    }

    // every unit as (kind, how many base units it is, the name to show)
    static readonly Dictionary<string, (string kind, double factor, string name)> Units = Build();

    static Dictionary<string, (string, double, string)> Build()
    {
        var d = new Dictionary<string, (string, double, string)>(StringComparer.OrdinalIgnoreCase);
        void Add(string kind, double factor, string name, params string[] spellings)
        {
            foreach (var s in spellings) d[s] = (kind, factor, name);
        }

        Add("length", 0.001, "mm", "mm", "millimeter", "millimeters", "millimetre", "millimetres");
        Add("length", 0.01, "cm", "cm", "centimeter", "centimeters", "centimetre", "centimetres");
        Add("length", 1, "m", "m", "meter", "meters", "metre", "metres");
        Add("length", 1000, "km", "km", "kilometer", "kilometers", "kilometre", "kilometres");
        Add("length", 0.0254, "in", "in", "inch", "inches", "\"");
        Add("length", 0.3048, "ft", "ft", "foot", "feet", "'");
        Add("length", 0.9144, "yd", "yd", "yard", "yards");
        Add("length", 1609.344, "mi", "mi", "mile", "miles");

        Add("weight", 0.001, "g", "g", "gram", "grams");
        Add("weight", 1, "kg", "kg", "kilo", "kilos", "kilogram", "kilograms");
        Add("weight", 0.028349523125, "oz", "oz", "ounce", "ounces");
        Add("weight", 0.45359237, "lb", "lb", "lbs", "pound", "pounds");
        Add("weight", 1000, "t", "t", "ton", "tons", "tonne", "tonnes");

        Add("volume", 0.001, "ml", "ml", "milliliter", "milliliters", "millilitre", "millilitres");
        Add("volume", 1, "L", "l", "liter", "liters", "litre", "litres");
        Add("volume", 0.2365882365, "cups", "cup", "cups");
        Add("volume", 3.785411784, "gal", "gal", "gallon", "gallons");

        Add("time", 1, "s", "s", "sec", "secs", "second", "seconds");
        Add("time", 60, "min", "min", "mins", "minute", "minutes");
        Add("time", 3600, "h", "h", "hr", "hrs", "hour", "hours");
        Add("time", 86400, "days", "d", "day", "days");
        Add("time", 604800, "weeks", "week", "weeks");

        Add("data", 1, "KB", "kb");
        Add("data", 1024, "MB", "mb");
        Add("data", 1024 * 1024, "GB", "gb");
        Add("data", 1024.0 * 1024 * 1024, "TB", "tb");

        Add("speed", 1, "km/h", "kmh", "km/h", "kph");
        Add("speed", 1.609344, "mph", "mph");
        Add("speed", 3.6, "m/s", "m/s", "mps");

        Add("temp", 0, "°C", "c", "°c", "celsius");
        Add("temp", 0, "°F", "f", "°f", "fahrenheit");
        Add("temp", 0, "K", "k", "kelvin");
        return d;
    }

    /// <summary>Lengths, weights, volumes, time, data sizes, speeds, and temperatures.</summary>
    public static bool TryConvertUnits(double amount, string from, string to, out string answer)
    {
        answer = "";
        if (!Units.TryGetValue(from, out var a) || !Units.TryGetValue(to, out var b) || a.kind != b.kind) return false;

        double result;
        if (a.kind == "temp")
        {
            double celsius = a.name switch { "°F" => (amount - 32) * 5 / 9, "K" => amount - 273.15, _ => amount };
            result = b.name switch { "°F" => celsius * 9 / 5 + 32, "K" => celsius + 273.15, _ => celsius };
        }
        else
        {
            result = amount * a.factor / b.factor;
        }

        answer = $"{Show(Math.Round(result, 4))} {b.name}";
        return true;
    }

    // ---------- money ----------

    static readonly Dictionary<string, string> Money = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$"] = "USD", ["usd"] = "USD", ["dollar"] = "USD", ["dollars"] = "USD", ["דולר"] = "USD",
        ["€"] = "EUR", ["eur"] = "EUR", ["euro"] = "EUR", ["euros"] = "EUR", ["אירו"] = "EUR", ["יורו"] = "EUR",
        ["₪"] = "ILS", ["ils"] = "ILS", ["nis"] = "ILS", ["shekel"] = "ILS", ["shekels"] = "ILS", ["שקל"] = "ILS", ["שקלים"] = "ILS", ["שח"] = "ILS",
        ["£"] = "GBP", ["gbp"] = "GBP", ["pound sterling"] = "GBP",
        ["¥"] = "JPY", ["jpy"] = "JPY", ["yen"] = "JPY",
        ["cad"] = "CAD", ["aud"] = "AUD", ["chf"] = "CHF", ["cny"] = "CNY", ["inr"] = "INR", ["sek"] = "SEK", ["nok"] = "NOK",
        ["dkk"] = "DKK", ["pln"] = "PLN", ["try"] = "TRY", ["brl"] = "BRL", ["mxn"] = "MXN", ["krw"] = "KRW", ["nzd"] = "NZD",
        ["czk"] = "CZK", ["huf"] = "HUF", ["ron"] = "RON", ["zar"] = "ZAR", ["sgd"] = "SGD", ["hkd"] = "HKD", ["thb"] = "THB",
    };

    /// <summary>The three-letter code for a currency word or sign, or null.</summary>
    public static string? Currency(string word) => Money.TryGetValue(word.Trim(), out var code) ? code : null;

    // ---------- "10m", "timer 25" ----------

    static readonly Regex TimerAsk = new(@"^\s*(?:timer\s+)?(\d{1,3})\s*(m|min|mins|minutes?|דק|דקות)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Minutes asked for by "10m", "10 min", or "timer 10". A bare number doesn't count.</summary>
    public static int? TimerMinutes(string text)
    {
        var m = TimerAsk.Match(text);
        if (!m.Success) return null;
        bool saidTimer = text.TrimStart().StartsWith("timer", StringComparison.OrdinalIgnoreCase);
        if (!saidTimer && !m.Groups[2].Success) return null;
        int minutes = int.Parse(m.Groups[1].Value, En);
        return minutes is >= 1 and <= 180 ? minutes : null;
    }
}
