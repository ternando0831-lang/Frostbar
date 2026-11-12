using System.Globalization;
using System.Text;

namespace Frostbar.Core.Search;

/// <summary>
/// The search's unit conversions (as Spotlight's): "10 km", "10km in mi", "70 F", "5 ft to cm", "100 kg を lb",
/// "3 cups in ml", "20 坪", "1.5 GB in MB", "90 min in h", full-width too. With no unit to convert to, metric goes to
/// imperial and imperial to metric (data sizes and times need one). Data sizes count in 1024s, as Windows shows them.
/// </summary>
public static class UnitConverter
{
    public enum Quantity { Length, Mass, Temperature, Volume, Speed, Area, Data, Time }

    /// <summary>A unit: what it measures, how many base units it is (m, kg, °C, L, m/s, m², byte, s), and its names.</summary>
    public sealed record Unit(Quantity Quantity, string Symbol, double Factor, bool Metric, string[] Names);

    /// <param name="Expression">What was converted, tidied ("10 km").</param>
    /// <param name="Result">The answer with its unit ("6.2137 mi").</param>
    /// <param name="Number">The answer alone, to copy ("6.2137").</param>
    public sealed record Conversion(string Expression, string Result, string Number);

    static readonly Unit[] Units =
    [
        new(Quantity.Length, "mm", 0.001, true, ["mm", "millimeter", "millimeters", "millimetre", "millimetres", "ミリ", "ミリメートル"]),
        new(Quantity.Length, "cm", 0.01, true, ["cm", "centimeter", "centimeters", "centimetre", "centimetres", "センチ", "センチメートル"]),
        new(Quantity.Length, "m", 1, true, ["m", "meter", "meters", "metre", "metres", "メートル"]),
        new(Quantity.Length, "km", 1000, true, ["km", "kilometer", "kilometers", "kilometre", "kilometres", "キロメートル"]),
        new(Quantity.Length, "in", 0.0254, false, ["in", "inch", "inches", "\"", "インチ"]),
        new(Quantity.Length, "ft", 0.3048, false, ["ft", "foot", "feet", "'", "フィート"]),
        new(Quantity.Length, "yd", 0.9144, false, ["yd", "yard", "yards", "ヤード"]),
        new(Quantity.Length, "mi", 1609.344, false, ["mi", "mile", "miles", "マイル"]),
        new(Quantity.Mass, "mg", 1e-6, true, ["mg", "milligram", "milligrams", "ミリグラム"]),
        new(Quantity.Mass, "g", 0.001, true, ["g", "gram", "grams", "gramme", "グラム"]),
        new(Quantity.Mass, "kg", 1, true, ["kg", "kilogram", "kilograms", "kilo", "kilos", "キログラム"]),
        new(Quantity.Mass, "t", 1000, true, ["t", "tonne", "tonnes", "トン"]),
        new(Quantity.Mass, "oz", 0.028349523125, false, ["oz", "ounce", "ounces", "オンス"]),
        new(Quantity.Mass, "lb", 0.45359237, false, ["lb", "lbs", "pound", "pounds", "ポンド"]),
        new(Quantity.Temperature, "°C", 1, true, ["c", "°c", "℃", "celsius", "摂氏", "度"]),
        new(Quantity.Temperature, "°F", 1, false, ["f", "°f", "℉", "fahrenheit", "華氏"]),
        new(Quantity.Temperature, "K", 1, true, ["kelvin", "ケルビン"]),
        new(Quantity.Volume, "ml", 0.001, true, ["ml", "milliliter", "milliliters", "millilitre", "millilitres", "ミリリットル"]),
        new(Quantity.Volume, "L", 1, true, ["l", "liter", "liters", "litre", "litres", "リットル"]),
        new(Quantity.Volume, "gal", 3.785411784, false, ["gal", "gallon", "gallons", "ガロン"]),
        new(Quantity.Volume, "qt", 0.946352946, false, ["qt", "quart", "quarts"]),
        new(Quantity.Volume, "pt", 0.473176473, false, ["pt", "pint", "pints"]),
        new(Quantity.Volume, "cup", 0.2365882365, false, ["cup", "cups", "カップ"]),
        new(Quantity.Volume, "fl oz", 0.0295735295625, false, ["fl oz", "floz", "fluid ounce", "fluid ounces"]),
        new(Quantity.Volume, "tbsp", 0.01478676478125, false, ["tbsp", "tablespoon", "tablespoons"]),
        new(Quantity.Volume, "tsp", 0.00492892159375, false, ["tsp", "teaspoon", "teaspoons"]),
        new(Quantity.Speed, "km/h", 1 / 3.6, true, ["km/h", "kmh", "kph", "km/hr"]),
        new(Quantity.Speed, "m/s", 1, true, ["m/s", "mps"]),
        new(Quantity.Speed, "mph", 0.44704, false, ["mph", "mi/h"]),
        new(Quantity.Speed, "kn", 1852 / 3600.0, false, ["kn", "kt", "knot", "knots", "ノット"]),
        new(Quantity.Area, "m²", 1, true, ["m2", "m²", "sqm", "平方メートル", "平米"]),
        new(Quantity.Area, "km²", 1e6, true, ["km2", "km²", "平方キロメートル"]),
        new(Quantity.Area, "ha", 1e4, true, ["ha", "hectare", "hectares", "ヘクタール"]),
        new(Quantity.Area, "坪", 400 / 121.0, true, ["坪", "tsubo"]),
        new(Quantity.Area, "ft²", 0.09290304, false, ["ft2", "ft²", "sqft", "sq ft", "square feet", "square foot"]),
        new(Quantity.Area, "acre", 4046.8564224, false, ["acre", "acres", "ac", "エーカー"]),
        new(Quantity.Data, "B", 1, true, ["byte", "bytes", "バイト"]),
        new(Quantity.Data, "KB", 1024, true, ["kb", "kib", "kilobyte", "kilobytes"]),
        new(Quantity.Data, "MB", 1048576, true, ["mb", "mib", "megabyte", "megabytes"]),
        new(Quantity.Data, "GB", 1073741824, true, ["gb", "gib", "gigabyte", "gigabytes"]),
        new(Quantity.Data, "TB", 1099511627776, true, ["tb", "tib", "terabyte", "terabytes"]),
        new(Quantity.Time, "ms", 0.001, true, ["ms", "millisecond", "milliseconds", "ミリ秒"]),
        new(Quantity.Time, "s", 1, true, ["s", "sec", "secs", "second", "seconds", "秒"]),
        new(Quantity.Time, "min", 60, true, ["min", "mins", "minute", "minutes", "分"]),
        new(Quantity.Time, "h", 3600, true, ["h", "hr", "hrs", "hour", "hours", "時間"]),
        new(Quantity.Time, "day", 86400, true, ["d", "day", "days", "日"]),
        new(Quantity.Time, "week", 604800, true, ["week", "weeks", "週間"]),
    ];

    /// <summary>Where a unit goes when the query names no unit to convert to (metric to imperial and back).</summary>
    static readonly Dictionary<string, string> Defaults = new()
    {
        ["mm"] = "in", ["cm"] = "in", ["m"] = "ft", ["km"] = "mi", ["in"] = "cm", ["ft"] = "m", ["yd"] = "m", ["mi"] = "km",
        ["mg"] = "oz", ["g"] = "oz", ["kg"] = "lb", ["t"] = "lb", ["oz"] = "g", ["lb"] = "kg",
        ["°C"] = "°F", ["°F"] = "°C", ["K"] = "°C",
        ["ml"] = "fl oz", ["L"] = "gal", ["gal"] = "L", ["qt"] = "L", ["pt"] = "ml", ["cup"] = "ml", ["fl oz"] = "ml", ["tbsp"] = "ml", ["tsp"] = "ml",
        ["km/h"] = "mph", ["m/s"] = "km/h", ["mph"] = "km/h", ["kn"] = "km/h",
        ["m²"] = "ft²", ["km²"] = "acre", ["ha"] = "acre", ["坪"] = "m²", ["ft²"] = "m²", ["acre"] = "m²",
    };

    // Words between the amount and the unit to convert to; Japanese particles after it.
    static readonly string[] Separators = [" to ", " in ", " into ", " as ", "を", "は", "→", "->", "=", "から"];
    static readonly string[] Trailers = ["に換算", "に", "で", "へ", "は?", "は？", "?", "？"];

    static Unit? Find(string name)
    {
        string n = name.Trim();
        if (n.Length == 0) return null;
        foreach (var u in Units)
            if (u.Names.Contains(n)) return u;
        return null;
    }

    /// <summary>
    /// The units that are words, written in Japanese after the amount with no space ("1.5時間": "7 day" read oddly in
    /// Japanese); the symbols (km, kg, °C, ml...) are the same in both languages.
    /// </summary>
    static readonly Dictionary<string, string> JaWords = new()
    {
        ["ms"] = "ミリ秒", ["s"] = "秒", ["min"] = "分", ["h"] = "時間", ["day"] = "日", ["week"] = "週間", ["cup"] = "カップ", ["acre"] = "エーカー",
    };

    /// <summary>An amount with its unit as the UI's language writes it ("7 days", "7日", "10 km").</summary>
    static string Written(string number, Unit u, string language)
    {
        if (language == "ja") return JaWords.TryGetValue(u.Symbol, out var word) ? number + word : $"{number} {u.Symbol}";
        bool plural = u.Symbol is "day" or "week" or "cup" or "acre" && number != "1";
        return $"{number} {u.Symbol}{(plural ? "s" : "")}";
    }

    /// <param name="language">The UI's language ("ja" or "en"): how the units that are words are written.</param>
    public static bool TryConvert(string? input, out Conversion conversion, string language = "en")
    {
        conversion = null!;
        string s = (input ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant().Trim();
        if (s.Length < 2 || s.Length > 60) return false;

        // The amount: a number at the start ("-12.5", "1,500", ".5").
        int i = 0;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        int digitsFrom = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == ',' && i + 1 < s.Length && char.IsDigit(s[i + 1]))) i++;
        if (i == digitsFrom || !s[digitsFrom..i].Any(char.IsDigit)) return false;
        if (!double.TryParse(s[..i].Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)) return false;
        string rest = s[i..].Trim();
        if (rest.Length == 0) return false;

        // The unit, and maybe the one to convert to.
        string fromName = rest, toName = "";
        foreach (var sep in Separators)
        {
            // After the unit, not as it (" in " is also the inch: "10 in" and "12 in in cm" read no unit before it).
            string padded = " " + rest + " ";
            int at = padded.IndexOf(sep, 1, StringComparison.Ordinal);
            if (at < 0) continue;
            fromName = padded[..at].Trim();
            toName = padded[(at + sep.Length)..].Trim();
            break;
        }
        foreach (var t in Trailers)
            if (toName.EndsWith(t, StringComparison.Ordinal)) { toName = toName[..^t.Length].Trim(); break; }

        var from = Find(fromName);
        if (from == null) return false;
        Unit? to;
        if (toName.Length > 0)
        {
            to = Find(toName);
            if (to == null || to.Quantity != from.Quantity || ReferenceEquals(to, from)) return false;
        }
        else
        {
            if (!Defaults.TryGetValue(from.Symbol, out var symbol)) return false;
            to = Units.First(u => u.Symbol == symbol);
        }

        double value = from.Quantity == Quantity.Temperature
            ? FromCelsius(ToCelsius(amount, from.Symbol), to.Symbol)
            : amount * from.Factor / to.Factor;
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        string number = Format(value);
        conversion = new Conversion(Written(Format(amount), from, language), Written(number, to, language), number);
        return true;
    }

    static double ToCelsius(double v, string unit) => unit switch { "°F" => (v - 32) * 5 / 9, "K" => v - 273.15, _ => v };
    static double FromCelsius(double c, string unit) => unit switch { "°F" => c * 9 / 5 + 32, "K" => c + 273.15, _ => c };

    /// <summary>Six significant digits at most, four decimals at most, thousands separated ("6.2137", "1,609.34", "0.0001").</summary>
    public static string Format(double v)
    {
        if (v == 0) return "0";
        double r = double.Parse(v.ToString("G6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (Math.Abs(r) >= 1e15 || Math.Abs(r) < 1e-4) return r.ToString("G6", CultureInfo.InvariantCulture);
        return r.ToString("#,0.####", CultureInfo.InvariantCulture);
    }
}
