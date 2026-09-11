using System.Globalization;

namespace TrMarketplaceHubDesktop.Catalog;
/// <summary>Pure decimal expression interpreter. Supports arithmetic, comparisons, AND/OR,
/// and searched (including nested) CASE with mandatory ELSE. No SQL or external access.</summary>
public static class PriceFormula
{
    public const string Example = "case when (x+100/1.20)/0.52*1.20<=200 then (x+100/1.20)/0.52*1.20 when (x+115/1.20)/0.52*1.20>=201 and (x+115/1.20)/0.52*1.20<=350 then (x+115/1.20)/0.52*1.20 when (x+145/1.20)/0.52*1.20>=351 and (x+145/1.20)/0.52*1.20<=800 then (x+145/1.20)/0.52*1.20 when (x+195/1.20)/0.52*1.20>=801 and (x+195/1.20)/0.52*1.20<=1500 then (x+195/1.20)/0.52*1.20 else (x+240/1.20)/0.52*1.20 end";
    public static CompiledPriceFormula Compile(string formula) => new(new Parser(formula).Parse());
    public static decimal Evaluate(string formula, decimal x) => Compile(formula).Evaluate(x);

    private sealed record Node(Func<decimal, decimal> Run, bool Boolean = false, int Depth = 1);

    private sealed class Parser
    {
        private readonly List<string> tokens = new();
        private int position;
        private int recursion;
        private string Current => position < tokens.Count ? tokens[position] : "<end>";
        private FormatException Error(string message) => new($"Formül hatası (öğe {position + 1}): {message}");

        public Parser(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 8192)
                throw new FormatException("Formül boş olamaz ve 8192 karakteri aşamaz.");
            for (var i = 0; i < text.Length;)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                var start = i;
                if (char.IsAsciiLetter(text[i]))
                {
                    while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
                    var word = text[start..i].ToLowerInvariant();
                    if (word is not ("x" or "case" or "when" or "then" or "else" or "end" or "and" or "or"))
                        throw Error($"İzin verilmeyen ad: {word}");
                }
                else if (char.IsAsciiDigit(text[i]) || text[i] == '.')
                {
                    while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.')) i++;
                }
                else if ("+-*/()=<>!".Contains(text[i]))
                {
                    i++;
                    if (i < text.Length && (text[start..(i+1)] is "<=" or ">=" or "<>" or "!=" or "==")) i++;
                    if (text[start..i] == "!") throw Error("! tek başına kullanılamaz.");
                }
                else throw Error($"İzin verilmeyen karakter: {text[i]}");
                tokens.Add(text[start..i].ToLowerInvariant());
                if (tokens.Count > 2048) throw Error("En fazla 2048 öğe kullanılabilir.");
            }
        }

        public Func<decimal, decimal> Parse()
        {
            var result = Expression();
            RequireType(result, false);
            if (position != tokens.Count) throw Error($"Beklenmeyen öğe: {Current}");
            return result.Run;
        }
        private bool Take(string token)
        {
            if (Current != token) return false;
            position++; return true;
        }
        private void Expect(string token) { if (!Take(token)) throw Error($"'{token}' bekleniyor."); }
        private void RequireType(Node node, bool boolean)
        {
            if (node.Boolean != boolean) throw Error(boolean ? "Karşılaştırma koşulu bekleniyor." : "Sayısal ifade bekleniyor.");
        }
        private Node Make(Func<decimal,decimal> run, bool boolean, params Node[] children)
        {
            var depth = 1 + children.Max(n => n.Depth);
            if (depth > 64) throw Error("İfade derinliği 64 sınırını aşamaz.");
            return new Node(run, boolean, depth);
        }
        private static int Precedence(string op) => op switch
        {
            "or" => 1, "and" => 2,
            "=" or "==" or "!=" or "<>" or "<" or ">" or "<=" or ">=" => 3,
            "+" or "-" => 4, "*" or "/" => 5, _ => 0
        };
        private Node Expression(int minimum = 1)
        {
            if (++recursion > 64) throw Error("İfade derinliği 64 sınırını aşamaz.");
            try
            {
                var left = Primary();
                while (Precedence(Current) >= minimum)
                {
                    var op = Current; position++;
                    var right = Expression(Precedence(op) + 1);
                    left = Binary(op, left, right);
                }
                return left;
            }
            finally { recursion--; }
        }
        private Node Primary()
        {
            if (Current is "+" or "-")
            {
                var negative = Take("-"); if (!negative) Expect("+");
                var operand = Expression(6); RequireType(operand, false);
                return Make(x => negative ? checked(-operand.Run(x)) : operand.Run(x), false, operand);
            }
            if (Take("(")) { var node = Expression(); Expect(")"); return node; }
            if (Take("case"))
            {
                var branches = new List<(Node Condition, Node Value)>();
                do
                {
                    Expect("when"); var condition = Expression(); RequireType(condition, true);
                    Expect("then"); var value = Expression(); RequireType(value, false);
                    branches.Add((condition, value));
                } while (Current == "when");
                Expect("else"); var fallback = Expression(); RequireType(fallback, false); Expect("end");
                return Make(x =>
                {
                    foreach (var branch in branches) if (branch.Condition.Run(x) != 0) return branch.Value.Run(x);
                    return fallback.Run(x);
                }, false, branches.SelectMany(b => new[]{b.Condition,b.Value}).Append(fallback).ToArray());
            }
            if (Take("x")) return new Node(x => x);
            if (!decimal.TryParse(Current, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                throw Error($"Geçersiz sayı veya öğe: {Current}");
            position++; return new Node(_ => number);
        }
        private Node Binary(string op, Node left, Node right)
        {
            var logical = op is "and" or "or";
            RequireType(left, logical); RequireType(right, logical);
            return Make(x =>
            {
                var a = left.Run(x);
                if (op == "and") return a != 0 && right.Run(x) != 0 ? 1m : 0m;
                if (op == "or") return a != 0 || right.Run(x) != 0 ? 1m : 0m;
                var b = right.Run(x);
                return op switch
                {
                    "+" => checked(a+b), "-" => checked(a-b), "*" => checked(a*b), "/" => a/b,
                    "=" or "==" => a==b ? 1m : 0m, "!=" or "<>" => a!=b ? 1m : 0m,
                    "<" => a<b ? 1m : 0m, ">" => a>b ? 1m : 0m,
                    "<=" => a<=b ? 1m : 0m, ">=" => a>=b ? 1m : 0m,
                    _ => throw new InvalidOperationException("Bilinmeyen işlem.")
                };
            }, Precedence(op) <= 3, left, right);
        }
    }
}
public sealed class CompiledPriceFormula
{
    private readonly Func<decimal, decimal> expression;
    internal CompiledPriceFormula(Func<decimal, decimal> expression) => this.expression = expression;
    public decimal Evaluate(decimal x)
    {
        try { return expression(x); }
        catch (DivideByZeroException e) { throw new InvalidOperationException("Formülde sıfıra bölme oluştu.", e); }
        catch (OverflowException e) { throw new InvalidOperationException("Formül sonucu decimal sayı aralığını aşıyor.", e); }
    }
}
