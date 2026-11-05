using System.Globalization;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Formulas;

public sealed record FormulaParseResult(
    FormulaNode? Node, FormulaValueType Type, IReadOnlySet<long> FieldRefs, string? Error);

/// <summary>公式遞迴下降 parser；純函式，只看啟用中的欄位。Error 不含前綴。</summary>
public static class FormulaParser
{
    private const int MaxLength = 1000;
    private const int MaxDepth = 20;

    private sealed class ParseException(string message) : Exception(message);

    private enum Tk { Number, Field, Ident, Op, LParen, RParen, Comma, End }

    private readonly record struct Token(Tk Kind, string Text);

    public static FormulaParseResult Parse(string? expression, Module module)
    {
        var refs = new HashSet<long>();
        try
        {
            if (string.IsNullOrWhiteSpace(expression)) throw new ParseException("運算式是空的");
            if (expression.Length > MaxLength) throw new ParseException("運算式太長");
            var p = new Parser(Tokenize(expression), module, refs);
            var (node, type) = p.ParseAll();
            return new FormulaParseResult(node, type, refs, null);
        }
        catch (ParseException e)
        {
            return new FormulaParseResult(null, FormulaValueType.Number, new HashSet<long>(), e.Message);
        }
    }

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is >= '0' and <= '9')
            {
                var start = i;
                while (i < s.Length && s[i] is >= '0' and <= '9') i++;
                if (i < s.Length && s[i] == '.')
                {
                    i++;
                    if (i >= s.Length || s[i] is < '0' or > '9') throw new ParseException("語法錯誤");
                    while (i < s.Length && s[i] is >= '0' and <= '9') i++;
                }
                tokens.Add(new Token(Tk.Number, s[start..i]));
            }
            else if (c == '{')
            {
                var end = s.IndexOf('}', i);
                if (end < 0) throw new ParseException("語法錯誤");
                var inner = s[(i + 1)..end];
                if (inner.Length == 0 || !inner.All(char.IsAsciiDigit)) throw new ParseException("語法錯誤");
                tokens.Add(new Token(Tk.Field, inner));
                i = end + 1;
            }
            else if (char.IsAsciiLetter(c))
            {
                var start = i;
                while (i < s.Length && char.IsAsciiLetterOrDigit(s[i])) i++;
                tokens.Add(new Token(Tk.Ident, s[start..i]));
            }
            else if (c is '+' or '-' or '*' or '/' or '=')
            {
                tokens.Add(new Token(Tk.Op, c.ToString())); i++;
            }
            else if (c is '<' or '>')
            {
                if (i + 1 < s.Length && (s[i + 1] == '=' || (c == '<' && s[i + 1] == '>')))
                {
                    tokens.Add(new Token(Tk.Op, s.Substring(i, 2))); i += 2;
                }
                else { tokens.Add(new Token(Tk.Op, c.ToString())); i++; }
            }
            else if (c == '(') { tokens.Add(new Token(Tk.LParen, "(")); i++; }
            else if (c == ')') { tokens.Add(new Token(Tk.RParen, ")")); i++; }
            else if (c == ',') { tokens.Add(new Token(Tk.Comma, ",")); i++; }
            else throw new ParseException("語法錯誤");
        }
        tokens.Add(new Token(Tk.End, ""));
        return tokens;
    }

    private sealed class Parser(List<Token> tokens, Module module, HashSet<long> refs)
    {
        private static readonly HashSet<string> CompareOps = ["=", "<>", "<", "<=", ">", ">="];

        private int _pos;
        private int _depth;

        private Token Cur => tokens[_pos];

        public (FormulaNode, FormulaValueType) ParseAll()
        {
            var result = ParseComparison();
            if (Cur.Kind != Tk.End) throw Syntax();
            if (result.Type == FormulaValueType.Boolean) throw new ParseException("比較只能用在 IF 裡");
            return (result.Node, result.Type);
        }

        private static ParseException Syntax() => new("語法錯誤");

        private bool IsOp(params string[] ops) => Cur.Kind == Tk.Op && ops.Contains(Cur.Text);

        private void Expect(Tk kind)
        {
            if (Cur.Kind != kind) throw Syntax();
            _pos++;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw new ParseException("運算式太複雜");
        }

        private (FormulaNode Node, FormulaValueType Type) ParseComparison()
        {
            Enter();
            var left = ParseAdditive();
            if (Cur.Kind == Tk.Op && CompareOps.Contains(Cur.Text))
            {
                var op = Cur.Text;
                _pos++;
                var right = ParseAdditive();
                RequireNotBoolean(left.Type);
                if (left.Type != right.Type) throw new ParseException("比較的兩邊型別必須相同");
                left = (new CompareNode(op, left.Node, right.Node), FormulaValueType.Boolean);
            }
            _depth--;
            return left;
        }

        private (FormulaNode Node, FormulaValueType Type) ParseAdditive()
        {
            var left = ParseTerm();
            while (IsOp("+", "-"))
            {
                var op = Cur.Text[0];
                _pos++;
                left = Arithmetic(op, left, ParseTerm());
            }
            return left;
        }

        private (FormulaNode Node, FormulaValueType Type) ParseTerm()
        {
            var left = ParseUnary();
            while (IsOp("*", "/"))
            {
                var op = Cur.Text[0];
                _pos++;
                left = Arithmetic(op, left, ParseUnary());
            }
            return left;
        }

        private static (FormulaNode, FormulaValueType) Arithmetic(
            char op, (FormulaNode Node, FormulaValueType Type) l, (FormulaNode Node, FormulaValueType Type) r)
        {
            RequireNotBoolean(l.Type);
            RequireNotBoolean(r.Type);
            if (l.Type == FormulaValueType.Date || r.Type == FormulaValueType.Date)
                throw new ParseException("日期不能直接加減，請用 DATEADD");
            return (new BinaryNode(op, l.Node, r.Node), FormulaValueType.Number);
        }

        private (FormulaNode Node, FormulaValueType Type) ParseUnary()
        {
            if (!IsOp("-")) return ParsePrimary();
            Enter();
            _pos++;
            var operand = ParseUnary();
            _depth--;
            RequireNotBoolean(operand.Type);
            if (operand.Type == FormulaValueType.Date) throw new ParseException("日期不能直接加減，請用 DATEADD");
            return (new NegateNode(operand.Node), FormulaValueType.Number);
        }

        private (FormulaNode Node, FormulaValueType Type) ParsePrimary()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case Tk.Number:
                    _pos++;
                    if (!decimal.TryParse(t.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                        throw Syntax();
                    return (new NumberNode(n), FormulaValueType.Number);
                case Tk.Field:
                    _pos++;
                    return FieldRef(t.Text);
                case Tk.LParen:
                    _pos++;
                    var inner = ParseComparison();
                    Expect(Tk.RParen);
                    return inner;
                case Tk.Ident:
                    _pos++;
                    return ParseCall(t.Text.ToUpperInvariant());
                default:
                    throw Syntax();
            }
        }

        private (FormulaNode, FormulaValueType) FieldRef(string text)
        {
            if (!long.TryParse(text, out var id)) throw Syntax();
            var field = module.Blocks.SelectMany(b => b.Fields)
                .FirstOrDefault(f => f.Id == id && f.Status == FieldStatus.Active)
                ?? throw new ParseException($"欄位 {id} 不存在或未啟用");
            var type = FormulaFieldTypes.ValueTypeOf(field)
                ?? throw new ParseException($"欄位 {id} 的型別不能用在公式");
            refs.Add(id);
            return (new FieldRefNode(id), type);
        }

        private (FormulaNode, FormulaValueType) ParseCall(string name)
        {
            if (name is not ("ROUND" or "IF" or "DATEDIFF" or "DATEADD" or "TODAY"))
                throw new ParseException($"不支援的函式 {name}");
            Expect(Tk.LParen);
            var args = new List<(FormulaNode Node, FormulaValueType Type)>();
            if (Cur.Kind != Tk.RParen)
            {
                args.Add(ParseComparison());
                while (Cur.Kind == Tk.Comma) { _pos++; args.Add(ParseComparison()); }
            }
            Expect(Tk.RParen);

            var expected = name switch { "IF" => 3, "TODAY" => 0, _ => 2 };
            if (args.Count != expected) throw new ParseException($"{name} 的參數數量不正確");

            switch (name)
            {
                case "TODAY":
                    return (new TodayNode(), FormulaValueType.Date);
                case "ROUND":
                    RequireNumber(args[0].Type, name);
                    if (args[1].Node is not NumberNode { Value: var d } || d != decimal.Truncate(d) || d < 0 || d > 6)
                        throw new ParseException("ROUND 的位數必須是 0 到 6 的整數");
                    return (new RoundNode(args[0].Node, (int)d), FormulaValueType.Number);
                case "DATEDIFF":
                    RequireDate(args[0].Type, name);
                    RequireDate(args[1].Type, name);
                    return (new DateDiffNode(args[0].Node, args[1].Node), FormulaValueType.Number);
                case "DATEADD":
                    RequireDate(args[0].Type, name);
                    RequireNumber(args[1].Type, name);
                    return (new DateAddNode(args[0].Node, args[1].Node), FormulaValueType.Date);
                default: // IF
                    if (args[0].Type != FormulaValueType.Boolean)
                        throw new ParseException("IF 的第一個參數必須是比較");
                    RequireNotBoolean(args[1].Type);
                    RequireNotBoolean(args[2].Type);
                    if (args[1].Type != args[2].Type) throw new ParseException("IF 兩個結果的型別必須相同");
                    return (new IfNode(args[0].Node, args[1].Node, args[2].Node), args[1].Type);
            }
        }

        private static void RequireNotBoolean(FormulaValueType t)
        {
            if (t == FormulaValueType.Boolean) throw new ParseException("比較只能用在 IF 裡");
        }

        private static void RequireNumber(FormulaValueType t, string fn)
        {
            RequireNotBoolean(t);
            if (t != FormulaValueType.Number) throw new ParseException($"{fn} 的參數必須是數字");
        }

        private static void RequireDate(FormulaValueType t, string fn)
        {
            RequireNotBoolean(t);
            if (t != FormulaValueType.Date) throw new ParseException($"{fn} 的參數必須是日期");
        }
    }
}

/// <summary>欄位在公式裡的值型別；null 表示不能引用。</summary>
internal static class FormulaFieldTypes
{
    public static FormulaValueType? ValueTypeOf(Field f) => f.Type switch
    {
        FieldType.Integer or FieldType.Decimal or FieldType.Currency or FieldType.Percent => FormulaValueType.Number,
        FieldType.Date => FormulaValueType.Date,
        FieldType.Formula or FieldType.Rollup => f.Config.ResultType switch
        {
            "number" or "currency" => FormulaValueType.Number,
            "date" => FormulaValueType.Date,
            _ => null,
        },
        _ => null,
    };
}
