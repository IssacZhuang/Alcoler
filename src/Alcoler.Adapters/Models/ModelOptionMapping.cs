using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alcoler.Adapters.Models;

public static class ModelOptionMapping
{
    private const int MaxSourceLength = 32_768;
    private const int MaxNodes = 4_096;
    private const int MaxDepth = 64;
    private const int MaxCachedExpressions = 256;
    private static readonly HashSet<string> ProtectedKeys = ["model", "messages", "tools", "stream", "input"];
    private static readonly ConcurrentDictionary<string, Node> ParsedExpressions = new(StringComparer.Ordinal);

    public static void Validate(string expression) => Parse(expression);

    public static JsonObject Evaluate(string expression, string? reasoningLevel = null, int? maxOutputTokens = null)
    {
        if (reasoningLevel is { Length: > 256 })
            throw Error("reasoningLevel exceeds maximum length of 256", 0);
        var node = Parse(expression);
        var result = (JsonObject)EvaluateNode(node, reasoningLevel, maxOutputTokens)!;
        var counter = new CharCountingBufferWriter();
        using (var writer = new Utf8JsonWriter(counter))
            result.WriteTo(writer);
        if (counter.GetCharCount() > 262_144)
            throw Error("Mapping result exceeds maximum size of 262144 characters", 0);
        return result;
    }

    internal static void MergePatch(JsonObject body, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is null)
                body.Remove(key);
            else if (value is JsonObject child)
            {
                if (body[key] is not JsonObject target)
                    body[key] = target = new JsonObject();
                MergePatch(target, child);
            }
            else
                body[key] = value.DeepClone();
        }
    }

    private static Node Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaxSourceLength)
            throw Error("Expression must contain 1 to 32768 characters", 0);
        if (ParsedExpressions.TryGetValue(expression, out var cached))
            return cached;
        var node = new Parser(expression).Parse();
        CheckDepth(node, 0);
        CheckResult(node);
        if (ParsedExpressions.Count < MaxCachedExpressions)
            ParsedExpressions.TryAdd(expression, node);
        return node;
    }

    private static void CheckDepth(Node node, int depth)
    {
        if (depth > MaxDepth)
            throw Error("Expression exceeds maximum depth of 64", node.Offset);
        foreach (var child in node.Children)
            CheckDepth(child, depth + 1);
    }

    private static void CheckResult(Node node)
    {
        if (node.Kind == "conditional")
        {
            CheckResult(node.Children[1]);
            CheckResult(node.Children[2]);
            return;
        }
        if (node.Kind != "object")
            throw Error("Model option mapping must return a JSON object in every branch", node.Offset);
        foreach (var key in node.Keys)
            if (ProtectedKeys.Contains(key))
                throw Error($"Mapping cannot overwrite protected request key '{key}'", node.Offset);
    }

    private static JsonNode? EvaluateNode(Node node, string? reasoningLevel, int? maxOutputTokens)
    {
        JsonNode? Eval(Node child) => EvaluateNode(child, reasoningLevel, maxOutputTokens);
        switch (node.Kind)
        {
            case "literal": return node.Value?.DeepClone();
            case "reasoningLevel":
                return JsonValue.Create(reasoningLevel ?? throw Error("reasoningLevel requires an effective value", node.Offset));
            case "maxOutputTokens":
                return JsonValue.Create((double)(maxOutputTokens ?? throw Error("maxOutputTokens requires an effective value", node.Offset)));
            case "array": return new JsonArray(node.Children.Select(Eval).ToArray());
            case "object":
                {
                    var result = new JsonObject();
                    for (var i = 0; i < node.Keys.Length; i++)
                        result.Add(node.Keys[i], Eval(node.Children[i]));
                    return result;
                }
            case "conditional": return Eval(node.Children[Boolean(Eval(node.Children[0]), node.Offset) ? 1 : 2]);
            case "unary":
                {
                    var value = Eval(node.Children[0]);
                    return node.Operator == "!" ? JsonValue.Create(!Boolean(value, node.Offset))
                        : Number(node.Operator == "-" ? -Numeric(value, node.Offset) : Numeric(value, node.Offset), node.Offset);
                }
            case "binary":
                {
                    var left = Eval(node.Children[0]);
                    if (node.Operator == "&&")
                        return JsonValue.Create(Boolean(left, node.Offset) && Boolean(Eval(node.Children[1]), node.Offset));
                    if (node.Operator == "||")
                        return JsonValue.Create(Boolean(left, node.Offset) || Boolean(Eval(node.Children[1]), node.Offset));
                    var right = Eval(node.Children[1]);
                    if (node.Operator is "==" or "!=")
                        return JsonValue.Create(JsonNode.DeepEquals(left, right) == (node.Operator == "=="));
                    if (node.Operator == "+" && TryString(left, out var ls) && TryString(right, out var rs))
                    {
                        if (ls.Length + rs.Length > MaxSourceLength)
                            throw Error("String result exceeds maximum size", node.Offset);
                        return JsonValue.Create(ls + rs);
                    }
                    if (node.Operator is "<" or "<=" or ">" or ">=")
                    {
                        var cmp = TryString(left, out ls) && TryString(right, out rs)
                            ? string.CompareOrdinal(ls, rs)
                            : Numeric(left, node.Offset).CompareTo(Numeric(right, node.Offset));
                        return JsonValue.Create(node.Operator switch { "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, _ => cmp >= 0 });
                    }
                    var a = Numeric(left, node.Offset);
                    var b = Numeric(right, node.Offset);
                    return Number(node.Operator switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, "%" => a % b, _ => double.NaN }, node.Offset);
                }
            default: throw Error("Unsupported expression", node.Offset);
        }
    }

    private static bool TryString(JsonNode? node, out string value)
    {
        value = "";
        return node is JsonValue v && v.TryGetValue(out value!);
    }

    private static bool Boolean(JsonNode? node, int offset) => node is JsonValue v && v.TryGetValue<bool>(out var value)
        ? value : throw Error("Boolean operand required", offset);

    private static double Numeric(JsonNode? node, int offset) => node is JsonValue v && v.TryGetValue<double>(out var value)
        ? value : throw Error("Numeric operand required", offset);

    private static JsonValue Number(double value, int offset)
    {
        if (!double.IsFinite(value) || Math.Truncate(value) == value && Math.Abs(value) > 9_007_199_254_740_991)
            throw Error("Numeric value is not JSON-safe", offset);
        return JsonValue.Create(value)!;
    }

    private static ArgumentException Error(string message, int offset) => new($"{message} at offset {offset}.");

    /// <summary>
    /// Counts UTF-16 characters of the UTF-8 stream without materializing the JSON text;
    /// an incremental decoder keeps multi-byte sequences split across writer buffers exact.
    /// </summary>
    private sealed class CharCountingBufferWriter : IBufferWriter<byte>
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private byte[] _buffer = new byte[256];
        private int _charCount;

        public void Advance(int count) => _charCount += _decoder.GetCharCount(_buffer.AsSpan(0, count), flush: false);

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer;
        }

        public int GetCharCount() => _charCount + _decoder.GetCharCount([], flush: true);

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 1)
                sizeHint = 1;
            if (_buffer.Length < sizeHint)
                Array.Resize(ref _buffer, Math.Max(sizeHint, _buffer.Length * 2));
        }
    }

    private sealed record Node(string Kind, int Offset, JsonNode? Value = null, string? Operator = null, Node[]? Operands = null, string[]? Names = null)
    {
        public Node[] Children => Operands ?? [];
        public string[] Keys => Names ?? [];
    }

    private sealed record Token(string Kind, string Text, int Offset);

    private sealed class Parser(string source)
    {
        private int _cursor;
        private int _nodes;
        private int _depth;
        private Token? _token;
        private Token Current => _token ??= ReadToken();

        public Node Parse()
        {
            var result = Expression();
            if (Current.Kind != "end")
                throw Error("Unexpected token; calls and member access are not supported", Current.Offset);
            return result;
        }

        private Node Make(Node node)
        {
            if (++_nodes > MaxNodes)
                throw Error("Expression exceeds maximum size of 4096 nodes", node.Offset);
            return node;
        }

        private Node Expression(int minimum = 0)
        {
            if (++_depth > MaxDepth)
                throw Error("Expression exceeds maximum depth of 64", Current.Offset);
            try
            {
                var left = Primary();
                while (Precedence(Current.Text) is var precedence && precedence >= minimum)
                {
                    var op = Advance();
                    var right = Expression(precedence + 1);
                    left = Make(new Node("binary", op.Offset, Operator: op.Text, Operands: [left, right]));
                }
                if (minimum == 0 && Consume("?"))
                {
                    var yes = Expression();
                    Expect(":");
                    left = Make(new Node("conditional", left.Offset, Operands: [left, yes, Expression()]));
                }
                return left;
            }
            finally { _depth--; }
        }

        private Node Primary()
        {
            var token = Advance();
            if (token.Text is "!" or "-" or "+")
                return Make(new Node("unary", token.Offset, Operator: token.Text, Operands: [Expression(7)]));
            if (token.Kind == "string")
                return Make(new Node("literal", token.Offset, JsonValue.Create(token.Text)));
            if (token.Kind == "number")
            {
                if (!double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw Error("Invalid number literal", token.Offset);
                return Make(new Node("literal", token.Offset, Number(number, token.Offset)));
            }
            if (token.Kind == "identifier")
                return token.Text switch
                {
                    "reasoningLevel" or "maxOutputTokens" => Make(new Node(token.Text, token.Offset)),
                    "true" or "false" => Make(new Node("literal", token.Offset, JsonValue.Create(token.Text == "true"))),
                    "null" => Make(new Node("literal", token.Offset)),
                    _ => throw Error($"Unknown identifier '{token.Text}'; only reasoningLevel and maxOutputTokens are allowed", token.Offset),
                };
            if (token.Text == "(")
            {
                var result = Expression();
                Expect(")");
                return result;
            }
            if (token.Text is "{" or "[")
            {
                var children = new List<Node>();
                var keys = new List<string>();
                var close = token.Text == "{" ? "}" : "]";
                if (!Consume(close))
                {
                    do
                    {
                        if (token.Text == "{")
                        {
                            var key = Advance();
                            if (key.Kind != "string" || keys.Contains(key.Text, StringComparer.Ordinal))
                                throw Error("Object keys must be unique string literals", key.Offset);
                            keys.Add(key.Text);
                            Expect(":");
                        }
                        children.Add(Expression());
                    } while (Consume(","));
                    Expect(close);
                }
                return Make(new Node(token.Text == "{" ? "object" : "array", token.Offset, Operands: children.ToArray(), Names: keys.ToArray()));
            }
            throw Error("Expected a literal, allowed variable, array, or object", token.Offset);
        }

        private static int Precedence(string op) => op switch
        {
            "||" => 1,
            "&&" => 2,
            "==" or "!=" => 3,
            "<" or "<=" or ">" or ">=" => 4,
            "+" or "-" => 5,
            "*" or "/" or "%" => 6,
            _ => -1,
        };

        private bool Consume(string text)
        {
            if (Current.Text != text) return false;
            Advance();
            return true;
        }

        private void Expect(string text)
        {
            if (!Consume(text)) throw Error($"Expected '{text}'", Current.Offset);
        }

        private Token Advance()
        {
            var result = Current;
            _token = null;
            return result;
        }

        private Token ReadToken()
        {
            while (_cursor < source.Length && char.IsWhiteSpace(source[_cursor])) _cursor++;
            var start = _cursor;
            if (_cursor == source.Length) return new Token("end", "", start);
            var c = source[_cursor++];
            if (c is '\'' or '"')
            {
                var value = new StringBuilder();
                while (_cursor < source.Length)
                {
                    var ch = source[_cursor++];
                    if (ch == c) return new Token("string", value.ToString(), start);
                    if (ch is '\n' or '\r') throw Error("Unterminated string literal", start);
                    if (ch != '\\') { value.Append(ch); continue; }
                    if (_cursor == source.Length) break;
                    var escaped = source[_cursor++];
                    if (escaped == 'u')
                    {
                        if (_cursor + 4 > source.Length || !ushort.TryParse(source.AsSpan(_cursor, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            throw Error("Invalid unicode escape", _cursor);
                        value.Append((char)code);
                        _cursor += 4;
                    }
                    else value.Append(escaped switch
                    {
                        '\'' => '\'',
                        '"' => '"',
                        '\\' => '\\',
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => throw Error("Unsupported string escape", _cursor - 1),
                    });
                }
                throw Error("Unterminated string literal", start);
            }
            if (char.IsAsciiDigit(c))
            {
                if (c != '0')
                    while (_cursor < source.Length && char.IsAsciiDigit(source[_cursor])) _cursor++;
                if (_cursor < source.Length && source[_cursor] == '.')
                {
                    _cursor++;
                    var fraction = _cursor;
                    while (_cursor < source.Length && char.IsAsciiDigit(source[_cursor])) _cursor++;
                    if (_cursor == fraction) throw Error("Invalid number literal", start);
                }
                if (_cursor < source.Length && source[_cursor] is 'e' or 'E')
                {
                    _cursor++;
                    if (_cursor < source.Length && source[_cursor] is '+' or '-') _cursor++;
                    while (_cursor < source.Length && char.IsAsciiDigit(source[_cursor])) _cursor++;
                }
                return new Token("number", source[start.._cursor], start);
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                while (_cursor < source.Length && (char.IsAsciiLetterOrDigit(source[_cursor]) || source[_cursor] == '_')) _cursor++;
                return new Token("identifier", source[start.._cursor], start);
            }
            if (_cursor < source.Length && source.Substring(start, 2) is "&&" or "||" or "==" or "!=" or "<=" or ">=")
                return new Token("symbol", source[start..++_cursor], start);
            if ("{}[](),:?.+-*/%!<>".Contains(c)) return new Token("symbol", c.ToString(), start);
            throw Error($"Unsupported token '{c}'", start);
        }
    }
}
