using System.Globalization;
using System.Text;

namespace Frostbar.Core.Search;

/// <summary>
/// The calculator row of the search: arithmetic typed into it, full-width too ("１＋２×３" = 7). + − × ÷ (also * / and
/// x between numbers), ^ (power, right to left, above the sign: -2^2 = -4), % (remainder), ! (factorial), parentheses,
/// implicit multiplication ("2(3+4)", "2pi"), "1,000" thousands separators, a trailing "=", and sqrt √ sin cos tan
/// (radians) asin acos atan ln log (base 10) abs exp, pi π e. Only input with at least one operation is a calculation:
/// "2023" or "-5" alone is not.
/// </summary>
public static class Calculator
{
    public const int MaxLength = 200;

    public static bool TryEvaluate(string? input, out double value)
    {
        value = 0;
        // Each "(" and leading "-" is a call deeper: thousands pasted overflowed the stack (no catch stops that) and the
        // shell ended. 200 characters is more than any sum typed into a search.
        if (input is { Length: > MaxLength }) return false;
        string s = Prepare(input ?? "");
        if (s.Length == 0 || !s.Any(char.IsDigit) && !s.Contains("pi") && !s.Contains('π')) return false;
        var p = new Parser(s);
        try
        {
            double v = p.Expression();
            if (!p.AtEnd || p.Operations == 0 || double.IsNaN(v) || double.IsInfinity(v)) return false;
            value = v;
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>12 significant digits (0.1+0.2 shows 0.3), thousands separated, no exponent below 10^15.</summary>
    public static string Format(double v)
    {
        double r = double.Parse(v.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (r == 0) return "0";
        if (Math.Abs(r) >= 1e15 || Math.Abs(r) < 1e-9) return r.ToString("G12", CultureInfo.InvariantCulture);
        return r.ToString("#,0.###########", CultureInfo.InvariantCulture);
    }

    static string Prepare(string input)
    {
        string s = input.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(c switch
            {
                '×' or '✕' or '✖' or '∗' => '*',
                '÷' or '∕' => '/',
                '−' or '–' or '—' => '-',
                _ => c,
            });
        }
        while (sb.Length > 0 && sb[^1] == '=') sb.Length--;
        // Context-dependent signs: "2x3" (x), "1ー2" (the IME's long-vowel mark for minus), "1,000" (thousands).
        for (int i = 1; i + 1 < sb.Length; i++)
        {
            char before = sb[i - 1], after = sb[i + 1];
            bool numberBefore = char.IsDigit(before) || before == ')' || before == '.';
            bool numberAfter = char.IsDigit(after) || after == '(' || after == '.';
            if (sb[i] == 'x' && numberBefore && numberAfter) sb[i] = '*';
            else if (sb[i] == 'ー' && numberBefore && numberAfter) sb[i] = '-';
            else if (sb[i] == ',' && char.IsDigit(before) && char.IsDigit(after) && ThousandsGroup(sb, i)) { sb.Remove(i, 1); i--; }
        }
        return sb.ToString();
    }

    /// <summary>A comma followed by exactly three digits, then no more digits: "1,000" yes, "1,5" no.</summary>
    static bool ThousandsGroup(StringBuilder sb, int comma)
    {
        int digits = 0;
        for (int j = comma + 1; j < sb.Length && char.IsDigit(sb[j]); j++) digits++;
        return digits == 3;
    }

    sealed class Parser(string s)
    {
        int _i;
        public int Operations;
        public bool AtEnd => _i >= s.Length;
        char Peek => _i < s.Length ? s[_i] : '\0';

        public double Expression()
        {
            double v = Term();
            while (Peek is '+' or '-')
            {
                char op = s[_i++];
                double r = Term();
                v = op == '+' ? v + r : v - r;
                Operations++;
            }
            return v;
        }

        double Term()
        {
            double v = Unary();
            while (true)
            {
                if (Peek is '*' or '/' or '%')
                {
                    char op = s[_i++];
                    double r = Unary();
                    v = op == '*' ? v * r : op == '/' ? v / r : v % r;
                    Operations++;
                }
                else if (Peek == '(' || char.IsLetter(Peek) || Peek == 'π' || Peek == '√')
                {
                    v *= Unary(); // implicit multiplication: 2(3+4), 2pi
                    Operations++;
                }
                else return v;
            }
        }

        double Unary()
        {
            if (Peek == '-') { _i++; return -Unary(); }
            if (Peek == '+') { _i++; return Unary(); }
            return Power();
        }

        double Power()
        {
            double b = Postfix();
            if (Peek != '^') return b;
            _i++;
            double e = Unary(); // right to left, and the exponent may have a sign: 2^-1
            Operations++;
            return Math.Pow(b, e);
        }

        double Postfix()
        {
            double v = Primary();
            while (Peek == '!')
            {
                _i++;
                if (v < 0 || v > 170 || v != Math.Floor(v)) throw new FormatException("factorial of " + v);
                double f = 1;
                for (int k = 2; k <= (int)v; k++) f *= k;
                v = f;
                Operations++;
            }
            return v;
        }

        double Primary()
        {
            if (Peek == '(')
            {
                _i++;
                double v = Expression();
                if (Peek != ')') throw new FormatException("missing )");
                _i++;
                return v;
            }
            if (char.IsDigit(Peek) || Peek == '.')
            {
                int start = _i;
                while (char.IsDigit(Peek) || Peek == '.') _i++;
                if (!double.TryParse(s.AsSpan(start, _i - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n))
                    throw new FormatException("number");
                return n;
            }
            if (Peek == 'π') { _i++; return Math.PI; }
            if (Peek == '√') { _i++; Operations++; return Math.Sqrt(Postfix()); }
            if (char.IsLetter(Peek))
            {
                int start = _i;
                while (char.IsLetter(Peek)) _i++;
                string name = s[start.._i];
                switch (name)
                {
                    case "pi": return Math.PI;
                    case "e": return Math.E;
                }
                Func<double, double>? f = name switch
                {
                    "sqrt" => Math.Sqrt, "sin" => Math.Sin, "cos" => Math.Cos, "tan" => Math.Tan,
                    "asin" => Math.Asin, "acos" => Math.Acos, "atan" => Math.Atan,
                    "ln" => Math.Log, "log" => Math.Log10, "abs" => Math.Abs, "exp" => Math.Exp,
                    _ => null,
                };
                if (f == null || Peek != '(') throw new FormatException("unknown name " + name);
                Operations++;
                return f(Primary());
            }
            throw new FormatException("unexpected " + Peek);
        }
    }
}
