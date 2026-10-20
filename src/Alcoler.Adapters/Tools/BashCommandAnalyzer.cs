using System.Text;
using System.Text.RegularExpressions;

namespace Alcoler.Adapters.Tools;

public sealed record BashEnvAssignment(string Name, string Value);

public sealed record BashRedirect(string Operator, string Target);

/// <summary>
/// One simple command with its resolved argv, env-assignment prefix, redirects, and the
/// operator ("&&", "||", "|", "|&", "sequence") that precedes it in the command line.
/// </summary>
public sealed record BashCommandInvocation(
    IReadOnlyList<string> Argv,
    string Name,
    string CommandText,
    IReadOnlyList<BashEnvAssignment> EnvAssignments,
    IReadOnlyList<BashRedirect> Redirects,
    string? OperatorBefore)
{
    public bool HasAssignmentPrefix => EnvAssignments.Count > 0;
}

/// <summary>
/// Conservative single-line bash analysis; anything the scanner cannot model
/// (subshells, heredocs, control flow, backgrounding) is reported as unsupported
/// syntax so callers treat the command as opaque.
/// </summary>
public sealed record BashCommandAnalysis(
    IReadOnlyList<BashCommandInvocation> Commands,
    bool HasDynamicWords,
    bool HasRedirects,
    bool HasParseErrors,
    bool HasUnsupportedSyntax)
{
    public static readonly BashCommandAnalysis Empty = new([], false, false, false, false);

    public bool IsPermissionSafe => !HasParseErrors && !HasUnsupportedSyntax && !HasDynamicWords;
}

public static partial class BashCommandAnalyzer
{
    private const int MaxParseLength = 10_000;

    private static readonly string[] PipeOperators = ["&&", "||", "|", "|&"];

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "if", "then", "elif", "else", "fi", "for", "while", "until", "do", "done",
        "case", "esac", "in", "select", "function", "time", "coproc", "{", "}", "!",
    };

    public static BashCommandAnalysis Analyze(string command)
    {
        if (command.Trim().Length == 0)
            return BashCommandAnalysis.Empty;
        if (command.Length > MaxParseLength)
            return BashCommandAnalysis.Empty with { HasParseErrors = true };

        return new Parser(command).Parse();
    }

    private sealed partial class Parser(string source)
    {
        private readonly List<BashCommandInvocation> _commands = [];
        private readonly List<string> _argv = [];
        private readonly List<BashEnvAssignment> _env = [];
        private readonly List<BashRedirect> _redirects = [];
        private readonly StringBuilder _word = new();
        private int _index;

        private bool _hasDynamicWords;
        private bool _hasRedirects;
        private bool _hasParseErrors;
        private bool _hasUnsupportedSyntax;
        private bool _wordDynamic;
        private bool _wordQuoted;
        private bool _atWordBoundary = true;
        private string? _operatorBefore;
        private int _segmentStart;

        public BashCommandAnalysis Parse()
        {
            while (_index < source.Length)
            {
                if (_hasUnsupportedSyntax || _hasParseErrors)
                    break;
                var c = source[_index];
                if (c is ' ' or '\t')
                {
                    FinalizeWord();
                    _index++;
                }
                else if (c == '\n')
                {
                    FinalizeWord();
                    EndCommand("sequence", _index, 1);
                }
                else if (c == ';')
                {
                    if (Peek(1) == ';')
                    {
                        _hasUnsupportedSyntax = true;
                        break;
                    }
                    EndCommand("sequence", _index, 1);
                }
                else if (c == '#' && _atWordBoundary && _word.Length == 0)
                {
                    SkipComment();
                }
                else if (c == '\'')
                {
                    ReadSingleQuoted();
                }
                else if (c == '"')
                {
                    ReadDoubleQuoted();
                }
                else if (c == '\\')
                {
                    ReadEscape();
                }
                else if (c == '$')
                {
                    ReadExpansion(quoted: false);
                }
                else if (c == '`')
                {
                    ReadBackquoted();
                }
                else if (c is '>' or '<')
                {
                    ReadRedirect();
                }
                else if (c is '&' or '|')
                {
                    ReadListOperator();
                }
                else if (c is '(' or ')')
                {
                    _hasUnsupportedSyntax = true;
                }
                else
                {
                    _word.Append(c);
                    _atWordBoundary = false;
                    _index++;
                }
            }

            FinalizeWord();
            EndCommand(null, source.Length, 0);
            return new BashCommandAnalysis(
                _commands.AsReadOnly(),
                _hasDynamicWords,
                _hasRedirects,
                _hasParseErrors,
                _hasUnsupportedSyntax);
        }

        private void FinalizeWord()
        {
            if (_word.Length == 0)
                return;
            var word = _word.ToString();
            var quoted = _wordQuoted;
            _word.Clear();
            _wordQuoted = false;
            if (_wordDynamic)
            {
                _hasDynamicWords = true;
                _wordDynamic = false;
            }

            if (_argv.Count == 0 && !quoted && EnvAssignment().IsMatch(word))
            {
                var separator = word.IndexOf('=');
                _env.Add(new BashEnvAssignment(word[..separator], word[(separator + 1)..]));
                return;
            }

            if (_argv.Count == 0 && !quoted && ControlKeywords.Contains(word))
            {
                _hasUnsupportedSyntax = true;
                return;
            }

            _argv.Add(word);
        }

        private void EndCommand(string? nextOperator, int operatorStart, int operatorLength)
        {
            FinalizeWord();
            var empty = _argv.Count == 0 && _env.Count == 0 && _redirects.Count == 0;
            if (empty)
            {
                if (_operatorBefore is not null && PipeOperators.Contains(_operatorBefore))
                    _hasParseErrors = true;
                if (nextOperator is not null && PipeOperators.Contains(nextOperator))
                    _hasParseErrors = true;
            }
            else
            {
                var commandText = source[_segmentStart..operatorStart].Trim();
                _commands.Add(new BashCommandInvocation(
                    _argv.ToArray(),
                    _argv.Count > 0 ? _argv[0] : string.Empty,
                    commandText,
                    _env.ToArray(),
                    _redirects.ToArray(),
                    _operatorBefore));
                _argv.Clear();
                _env.Clear();
                _redirects.Clear();
            }

            _operatorBefore = nextOperator;
            _index = operatorStart + operatorLength;
            _segmentStart = _index;
            _atWordBoundary = true;
        }

        private void ReadListOperator()
        {
            var c = source[_index];
            if (c == '|')
            {
                if (Peek(1) == '|')
                    EndCommand("||", _index, 2);
                else if (Peek(1) == '&')
                    EndCommand("|&", _index, 2);
                else
                    EndCommand("|", _index, 1);
                return;
            }

            if (Peek(1) == '&')
            {
                EndCommand("&&", _index, 2);
                return;
            }
            if (Peek(1) == '>')
            {
                // "&>" redirects both streams to a file and is handled as a redirect.
                FinalizeWord();
                _index += 2;
                var target = ReadTargetWord();
                if (target is null)
                    _hasParseErrors = true;
                else
                {
                    _redirects.Add(new BashRedirect("&>", target));
                    _hasRedirects = true;
                }
                return;
            }

            // A bare "&" backgrounds the statement, which the model surface forbids.
            _hasUnsupportedSyntax = true;
        }

        private void ReadRedirect()
        {
            var fd = TakeLeadingFileDescriptor();
            var c = source[_index];
            string op;
            if (c == '>' && Peek(1) == '(')
            {
                // Process substitution runs a subshell before the main command.
                _index++;
                ReadParenthesized();
                _wordDynamic = true;
                _atWordBoundary = false;
                return;
            }
            if (c == '>')
            {
                if (Peek(1) == '&')
                {
                    op = ">&";
                    _index += 2;
                }
                else if (Peek(1) == '>')
                {
                    op = ">>";
                    _index += 2;
                }
                else if (Peek(1) == '|')
                {
                    op = ">|";
                    _index += 2;
                }
                else
                {
                    op = ">";
                    _index++;
                }
            }
            else if (Peek(1) == '(')
            {
                // Process substitution runs a subshell before the main command.
                _index++;
                ReadParenthesized();
                _wordDynamic = true;
                _atWordBoundary = false;
                return;
            }
            else if (Peek(1) == '(')
            {
                // Process substitution runs a subshell before the main command.
                _index++;
                ReadParenthesized();
                _wordDynamic = true;
                _atWordBoundary = false;
                return;
            }
            else if (Peek(1) == '<' && Peek(2) == '<')
            {
                op = "<<<";
                _index += 3;
            }
            else if (Peek(1) == '<')
            {
                _hasUnsupportedSyntax = true;
                return;
            }
            else if (Peek(1) == '&')
            {
                op = "<&";
                _index += 2;
            }
            else if (Peek(1) == '>')
            {
                op = "<>";
                _index += 2;
            }
            else
            {
                op = "<";
                _index++;
            }

            _ = fd;
            var target = ReadTargetWord();
            if (target is null)
            {
                _hasParseErrors = true;
                return;
            }
            _redirects.Add(new BashRedirect(op, target));
            _hasRedirects = true;
            _atWordBoundary = true;
        }

        private string? TakeLeadingFileDescriptor()
        {
            var word = _word.ToString();
            if (word.Length > 0 && word.All(char.IsDigit))
            {
                _word.Clear();
                return word;
            }
            return null;
        }

        private string? ReadTargetWord()
        {
            while (_index < source.Length && source[_index] is ' ' or '\t')
                _index++;
            var start = _index;
            while (_index < source.Length)
            {
                var c = source[_index];
                if (c is ' ' or '\t' or '\n' or ';' or '&' or '|' or '<' or '>' or '(' or ')' or '\'' or '"' or '$' or '`' or '\\')
                    break;
                _index++;
            }
            return _index > start ? source[start.._index] : null;
        }

        private void ReadSingleQuoted()
        {
            _wordQuoted = true;
            _atWordBoundary = false;
            _index++;
            var start = _index;
            while (_index < source.Length && source[_index] != '\'')
                _index++;
            if (_index >= source.Length)
            {
                _hasParseErrors = true;
                return;
            }
            _word.Append(source[start.._index]);
            _index++;
        }

        private void ReadDoubleQuoted()
        {
            _wordQuoted = true;
            _atWordBoundary = false;
            _index++;
            while (_index < source.Length)
            {
                var c = source[_index];
                if (c == '"')
                {
                    _index++;
                    return;
                }
                if (c == '\\')
                {
                    if (_index + 1 < source.Length)
                    {
                        _word.Append(source[_index + 1]);
                        _index += 2;
                    }
                    else
                    {
                        _hasParseErrors = true;
                        return;
                    }
                    continue;
                }
                if (c == '$')
                {
                    ReadExpansion(quoted: true);
                    continue;
                }
                if (c == '`')
                {
                    ReadBackquoted();
                    continue;
                }
                _word.Append(c);
                _index++;
            }

            _hasParseErrors = true;
        }

        private void ReadEscape()
        {
            if (_index + 1 >= source.Length)
            {
                _hasParseErrors = true;
                return;
            }
            var next = source[_index + 1];
            _index += 2;
            if (next != '\n')
            {
                _word.Append(next);
                _atWordBoundary = false;
            }
        }

        private void ReadExpansion(bool quoted)
        {
            _wordDynamic = true;
            _atWordBoundary = false;
            var next = Peek(1);
            if (next == '(')
            {
                _word.Append('$');
                _index++;
                ReadParenthesized();
                return;
            }
            if (next == '{')
            {
                var start = _index;
                _index += 2;
                var depth = 1;
                while (_index < source.Length && depth > 0)
                {
                    if (source[_index] == '{')
                        depth++;
                    else if (source[_index] == '}')
                        depth--;
                    _index++;
                }
                if (depth != 0)
                {
                    _hasParseErrors = true;
                    return;
                }
                _word.Append(source[start.._index]);
                return;
            }

            _word.Append('$');
            _index++;
            if (quoted)
                return;
            var nameStart = _index;
            while (_index < source.Length && (char.IsLetterOrDigit(source[_index]) || source[_index] == '_'))
                _index++;
            _word.Append(source[nameStart.._index]);
        }

        private void ReadBackquoted()
        {
            _wordDynamic = true;
            _atWordBoundary = false;
            _word.Append('`');
            _index++;
            var start = _index;
            while (_index < source.Length && source[_index] != '`')
                _index++;
            if (_index >= source.Length)
            {
                _hasParseErrors = true;
                return;
            }
            _word.Append(source[start.._index]);
            _index++;
            _word.Append('`');
        }

        private void ReadParenthesized()
        {
            // Consumes from '(' through the matching ')', appending the raw text; quote
            // characters inside are skipped so nested parens stay balanced.
            var start = _index;
            _index++;
            var depth = 1;
            while (_index < source.Length && depth > 0)
            {
                var c = source[_index];
                if (c == '\'')
                    SkipSingleQuoted();
                else if (c == '"')
                    SkipDoubleQuoted();
                else if (c == '(')
                {
                    depth++;
                    _index++;
                }
                else if (c == ')')
                {
                    depth--;
                    _index++;
                }
                else
                    _index++;
            }
            if (depth != 0)
            {
                _hasParseErrors = true;
                return;
            }
            _word.Append(source[start.._index]);
        }

        private void SkipSingleQuoted()
        {
            _index++;
            while (_index < source.Length && source[_index] != '\'')
                _index++;
            if (_index < source.Length)
                _index++;
        }

        private void SkipDoubleQuoted()
        {
            _index++;
            while (_index < source.Length && source[_index] != '"')
            {
                if (source[_index] == '\\' && _index + 1 < source.Length)
                    _index++;
                _index++;
            }
            if (_index < source.Length)
                _index++;
        }

        private void SkipComment()
        {
            while (_index < source.Length && source[_index] != '\n')
                _index++;
        }

        private char Peek(int offset) => _index + offset < source.Length ? source[_index + offset] : '\0';

        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*=")]
        private static partial Regex EnvAssignment();
    }
}
