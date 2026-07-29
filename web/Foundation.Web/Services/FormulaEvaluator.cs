using System.Globalization;

namespace Foundation.Web.Services;

/// <summary>
/// Tiny safe arithmetic evaluator for Formula custom fields. Supports
/// numbers, + - * / with normal precedence, parentheses, unary minus, the
/// functions round(x[,n]) / abs(x) / min(a,b) / max(a,b), and field
/// references — bare identifiers (NetWeight, Price) or [bracketed names]
/// for names with spaces ([Pizza Count]). Field values come from the
/// caller's resolver; a null (missing) value makes the whole result null
/// rather than guessing zero. Throws FormatException on bad syntax or an
/// unknown field, which is how CustomFieldsController validates formulas.
/// </summary>
public static class FormulaEvaluator
{
    public static double? Evaluate(string formula, Func<string, double?> resolve)
    {
        var parser = new Parser(formula, resolve);
        var result = parser.ParseExpression();
        parser.ExpectEnd();
        if (result is double d && (double.IsNaN(d) || double.IsInfinity(d))) return null;
        return result;
    }

    /// <summary>Syntax/reference check: resolves every field to 1 so only
    /// structure and field names are validated. Returns an error or null.</summary>
    public static string? Validate(string formula, Func<string, bool> fieldExists)
    {
        try
        {
            Evaluate(formula, name => fieldExists(name)
                ? 1
                : throw new FormatException($"Unknown field \"{name}\". Use NetWeight, GrossWeight, TareWeight, InWeight, OutWeight, NetTons, or a numeric custom field name in [brackets]."));
            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    private sealed class Parser
    {
        private readonly string _s;
        private readonly Func<string, double?> _resolve;
        private int _i;

        public Parser(string s, Func<string, double?> resolve)
        {
            _s = s ?? "";
            _resolve = resolve;
        }

        private void SkipWs() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

        private char Peek() { SkipWs(); return _i < _s.Length ? _s[_i] : '\0'; }

        public void ExpectEnd()
        {
            if (Peek() != '\0') throw new FormatException($"Unexpected \"{_s[_i]}\" at position {_i + 1}.");
        }

        // expr := term (('+'|'-') term)*
        public double? ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                var c = Peek();
                if (c == '+') { _i++; var r = ParseTerm(); left = left.HasValue && r.HasValue ? left + r : null; }
                else if (c == '-') { _i++; var r = ParseTerm(); left = left.HasValue && r.HasValue ? left - r : null; }
                else return left;
            }
        }

        // term := factor (('*'|'/') factor)*
        private double? ParseTerm()
        {
            var left = ParseFactor();
            while (true)
            {
                var c = Peek();
                if (c == '*') { _i++; var r = ParseFactor(); left = left.HasValue && r.HasValue ? left * r : null; }
                else if (c == '/') { _i++; var r = ParseFactor(); left = left.HasValue && r.HasValue ? left / r : null; }
                else return left;
            }
        }

        // factor := number | [name] | ident | ident '(' args ')' | '(' expr ')' | '-' factor
        private double? ParseFactor()
        {
            var c = Peek();
            if (c == '-') { _i++; var v = ParseFactor(); return v.HasValue ? -v : null; }
            if (c == '(')
            {
                _i++;
                var v = ParseExpression();
                if (Peek() != ')') throw new FormatException("Missing closing parenthesis.");
                _i++;
                return v;
            }
            if (c == '[')
            {
                _i++;
                var end = _s.IndexOf(']', _i);
                if (end < 0) throw new FormatException("Missing closing bracket for a field name.");
                var name = _s[_i..end].Trim();
                _i = end + 1;
                if (name.Length == 0) throw new FormatException("Empty field name in brackets.");
                return _resolve(name);
            }
            if (char.IsDigit(c) || c == '.')
            {
                SkipWs();
                var start = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                if (!double.TryParse(_s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    throw new FormatException($"Bad number \"{_s[start.._i]}\".");
                return n;
            }
            if (char.IsLetter(c) || c == '_')
            {
                SkipWs();
                var start = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                var name = _s[start.._i];
                if (Peek() == '(') return ParseFunction(name);
                return _resolve(name);
            }
            throw new FormatException(c == '\0' ? "Formula ends unexpectedly." : $"Unexpected \"{c}\" at position {_i + 1}.");
        }

        private double? ParseFunction(string name)
        {
            _i++; // consume '('
            var args = new List<double?>();
            if (Peek() != ')')
            {
                args.Add(ParseExpression());
                while (Peek() == ',') { _i++; args.Add(ParseExpression()); }
            }
            if (Peek() != ')') throw new FormatException($"Missing closing parenthesis for {name}(...).");
            _i++;

            double? Arg(int idx) => idx < args.Count ? args[idx] : null;
            bool AllPresent() => args.All(a => a.HasValue);

            switch (name.ToLowerInvariant())
            {
                case "round":
                    if (args.Count is < 1 or > 2) throw new FormatException("round() takes 1 or 2 arguments: round(x) or round(x, decimals).");
                    if (!AllPresent()) return null;
                    return Math.Round(Arg(0)!.Value, Math.Clamp((int)(Arg(1) ?? 0), 0, 10), MidpointRounding.AwayFromZero);
                case "abs":
                    if (args.Count != 1) throw new FormatException("abs() takes 1 argument.");
                    return Arg(0).HasValue ? Math.Abs(Arg(0)!.Value) : null;
                case "min":
                    if (args.Count != 2) throw new FormatException("min() takes 2 arguments.");
                    return AllPresent() ? Math.Min(Arg(0)!.Value, Arg(1)!.Value) : null;
                case "max":
                    if (args.Count != 2) throw new FormatException("max() takes 2 arguments.");
                    return AllPresent() ? Math.Max(Arg(0)!.Value, Arg(1)!.Value) : null;
                default:
                    throw new FormatException($"Unknown function \"{name}\". Available: round, abs, min, max.");
            }
        }
    }
}
