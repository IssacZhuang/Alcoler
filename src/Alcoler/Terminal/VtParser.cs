using System.Text;

namespace Alcoler.Terminal;

public abstract record VtToken;

/// <summary>A run of printable characters.</summary>
public sealed record VtText(string Text) : VtToken;

/// <summary>A C0 control character (CR, LF, BS, TAB, BEL, ...).</summary>
public sealed record VtControl(char Control) : VtToken;

/// <summary>A two-character escape sequence such as DECSC (ESC 7).</summary>
public sealed record VtEscape(char Final) : VtToken;

/// <summary>A CSI sequence with numeric parameters and its final byte.</summary>
public sealed record VtCsi(char Final, IReadOnlyList<int> Parameters, bool Private) : VtToken
{
    public int Param(int index, int defaultValue) =>
        index < Parameters.Count && Parameters[index] >= 0 ? Parameters[index] : defaultValue;

    // Record equality would compare the parameter list by reference; parameters must
    // compare element-wise.
    public bool Equals(VtCsi? other) =>
        other is not null && Final == other.Final && Private == other.Private
        && Parameters.SequenceEqual(other.Parameters);

    public override int GetHashCode() => HashCode.Combine(Final, Private);
}

/// <summary>An OSC string (title and similar); parsed but ignored by the buffer.</summary>
public sealed record VtOsc(string Payload) : VtToken;

/// <summary>
/// Incremental VT/ANSI parser. Incomplete sequences at the end of a chunk are
/// held in parser state and completed by the next chunk; <see cref="Feed"/>
/// returns only tokens that are fully resolved.
/// </summary>
public sealed class VtParser
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        StringSequence,
        StringSequenceEscape,
        Charset,
    }

    private State _state = State.Ground;
    private readonly StringBuilder _text = new();
    private readonly List<int> _parameters = [];
    private readonly StringBuilder _payload = new();
    private bool _csiPrivate;
    private char _escapeFinal;
    private char _csiIntermediate;

    public IReadOnlyList<VtToken> Feed(ReadOnlySpan<char> chunk)
    {
        var tokens = new List<VtToken>(chunk.Length / 8 + 1);
        foreach (var c in chunk)
        {
            FeedChar(c, tokens);
        }
        FlushText(tokens);
        return tokens;
    }

    private void FeedChar(char c, List<VtToken> tokens)
    {
        switch (_state)
        {
            case State.Ground:
                if (c == '\x1b')
                {
                    FlushText(tokens);
                    _state = State.Escape;
                }
                else if (c < ' ')
                {
                    FlushText(tokens);
                    tokens.Add(new VtControl(c));
                }
                else
                {
                    _text.Append(c);
                }
                break;

            case State.Escape:
                switch (c)
                {
                    case '[':
                        _state = State.Csi;
                        ResetCsi();
                        break;
                    case ']':
                        _state = State.Osc;
                        _payload.Clear();
                        break;
                    case 'P' or 'X' or '^' or '_':
                        _state = State.StringSequence;
                        _payload.Clear();
                        break;
                    case >= '(' and <= '+':
                        _state = State.Charset;
                        break;
                    case >= '\x20' and <= '\x2f':
                        _escapeFinal = c;
                        _state = State.EscapeIntermediate;
                        break;
                    case < ' ':
                        tokens.Add(new VtControl(c));
                        break;
                    default:
                        tokens.Add(new VtEscape(c));
                        _state = State.Ground;
                        break;
                }
                break;

            case State.EscapeIntermediate:
                if (c is >= '\x40' and <= '\x7e')
                {
                    tokens.Add(new VtEscape(c));
                    _state = State.Ground;
                }
                else if (c < '\x20')
                {
                    tokens.Add(new VtControl(c));
                }
                break;

            case State.Csi:
                if (c is >= '0' and <= '9')
                {
                    var value = c - '0';
                    var index = _parameters.Count - 1;
                    if (_parameters[index] >= 0)
                    {
                        _parameters[index] = _parameters[index] * 10 + value;
                    }
                    else
                    {
                        _parameters[index] = value;
                    }
                }
                else if (c == ';' || c == ':')
                {
                    // ':' separates sub-parameters; treated as a new parameter.
                    _parameters.Add(-1);
                }
                else if (c is '?' or '>' or '<' or '=')
                {
                    _csiPrivate = true;
                }
                else if (c is >= '\x20' and <= '\x2f')
                {
                    _csiIntermediate = c;
                }
                else if (c is >= '\x40' and <= '\x7e')
                {
                    if (_csiIntermediate == '\0')
                    {
                        tokens.Add(new VtCsi(c, _parameters.ToArray(), _csiPrivate));
                    }
                    // Sequences with intermediates (e.g. DECSCUSR variants) are dropped.
                    _state = State.Ground;
                }
                else if (c == '\x1b')
                {
                    _state = State.Escape;
                }
                else if (c < ' ')
                {
                    tokens.Add(new VtControl(c));
                }
                break;

            case State.Osc:
                if (c == '\a')
                {
                    tokens.Add(new VtOsc(_payload.ToString()));
                    _state = State.Ground;
                }
                else if (c == '\x1b')
                {
                    _state = State.OscEscape;
                }
                else
                {
                    _payload.Append(c);
                }
                break;

            case State.OscEscape:
                if (c == '\\')
                {
                    tokens.Add(new VtOsc(_payload.ToString()));
                    _state = State.Ground;
                }
                else
                {
                    // ESC not followed by '\' is a malformed terminator; keep collecting payload.
                    _payload.Append(c);
                    _state = State.Osc;
                }
                break;

            case State.StringSequence:
                if (c == '\x1b')
                {
                    _state = State.StringSequenceEscape;
                }
                else if (c == '\a')
                {
                    _state = State.Ground;
                }
                break;

            case State.StringSequenceEscape:
                _state = c == '\\' ? State.Ground : State.StringSequence;
                break;

            case State.Charset:
                _state = State.Ground;
                break;
        }
    }

    private void FlushText(List<VtToken> tokens)
    {
        if (_text.Length > 0)
        {
            tokens.Add(new VtText(_text.ToString()));
            _text.Clear();
        }
    }

    private void ResetCsi()
    {
        _parameters.Clear();
        // -1 marks an omitted parameter so "ESC[;5H" yields [default, 5].
        _parameters.Add(-1);
        _csiPrivate = false;
        _csiIntermediate = '\0';
    }
}
