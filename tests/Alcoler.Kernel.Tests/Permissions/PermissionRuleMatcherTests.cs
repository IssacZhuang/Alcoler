using System.Text.Json;
using Alcoler.Contracts.Permissions;
using Alcoler.Kernel.Permissions;
using Xunit;

namespace Alcoler.Kernel.Tests.Permissions;

public class PermissionRuleMatcherTests
{
    private static JsonElement Input(object? value) => JsonSerializer.SerializeToElement(value ?? new { });

    private static bool Matches(string ruleToolName, string? ruleContent, string toolName, object? input) =>
        PermissionRuleMatcher.Matches(new PermissionRule(ruleToolName, ruleContent, PermissionVerdict.Ask), toolName, Input(input));

    [Fact]
    public void ExactToolName_MatchesCaseInsensitively()
    {
        Assert.True(Matches("Bash", "git status", "bash", new { command = "git status" }));
    }

    [Fact]
    public void ExactToolName_DoesNotMatchOtherTool()
    {
        Assert.False(Matches("Grep", null, "Bash", new { command = "x" }));
    }

    [Theory]
    [InlineData("Git:*", "Git:commit", true)]
    [InlineData("Git:*", "git:status", true)]
    [InlineData("Git:*", "Grep", false)]
    [InlineData("*", "AnyToolName", true)]
    [InlineData("Bash", "Bash", true)]
    [InlineData("Bash", "bash", true)]
    [InlineData("Bash", "Bash2", false)]
    public void ToolNameMatching_ExactAndWildcard(string ruleToolName, string toolName, bool expected)
    {
        Assert.Equal(expected, Matches(ruleToolName, null, toolName, new { }));
    }

    [Fact]
    public void Content_MatchesSubjectFromFilePath()
    {
        Assert.True(Matches("Write", "src/*", "Write", new { file_path = "src/Program.cs" }));
        Assert.False(Matches("Write", "src/*", "Write", new { file_path = "build/Program.cs" }));
    }

    [Fact]
    public void Content_MatchesSubjectFromPatchText()
    {
        Assert.True(Matches("EditFile", "@@ -1 +1 @@", "EditFile", new { patch_text = "@@ -1 +1 @@" }));
    }

    [Fact]
    public void Content_PrefixWildcard_MatchesCommandPrefix()
    {
        Assert.True(Matches("Bash", "git push:*", "Bash", new { command = "git push --force" }));
    }

    [Fact]
    public void Content_ExactMatch_RequiresFullEquality()
    {
        Assert.True(Matches("Bash", "git status", "Bash", new { command = "git status" }));
        Assert.False(Matches("Bash", "git", "Bash", new { command = "git status" }));
    }

    [Fact]
    public void NullContent_MatchesAnyInvocation()
    {
        Assert.True(Matches("Bash", null, "Bash", new { command = "anything" }));
        Assert.True(Matches("Bash", null, "Bash", new { }));
    }

    [Fact]
    public void WildcardContent_MatchesAnyInvocation()
    {
        Assert.True(Matches("Bash", "*", "Bash", new { command = "anything" }));
        Assert.True(Matches("Bash", "*", "Bash", new { }));
    }

    [Fact]
    public void Content_WithNoSubject_DoesNotMatch()
    {
        Assert.False(Matches("Bash", "git status", "Bash", new { }));
        Assert.False(Matches("WebFetch", "https://x", "WebFetch", "not-an-object"));
    }

    [Fact]
    public void Subject_FirstPresentPropertyWins()
    {
        var urlAndFile = new { url = "https://alcoler.dev", file_path = "z" };
        Assert.True(Matches("WebFetch", "https://alcoler.dev", "WebFetch", urlAndFile));
        Assert.False(Matches("WebFetch", "z", "WebFetch", urlAndFile));

        Assert.True(Matches("Grep", "src", "Grep", new { file_path = "src", pattern = "*.cs" }));
    }

    [Fact]
    public void WriteRule_MatchesEditFileTool()
    {
        Assert.True(Matches("Write", "src/a.cs", "EditFile", new { file_path = "src/a.cs" }));
    }

    [Fact]
    public void EditFileRule_MatchesWriteTool()
    {
        Assert.True(Matches("EditFile", "src/a.cs", "Write", new { file_path = "src/a.cs" }));
    }

    [Fact]
    public void AliasedToolNames_StillRequireContentMatch()
    {
        Assert.False(Matches("Write", "src/a.cs", "EditFile", new { file_path = "other" }));
    }

    [Fact]
    public void ExtractSubject_PrefersCommandAndSkipsNonStringValues()
    {
        var element = JsonSerializer.SerializeToElement(new { command = "ls", file_path = "x" });
        Assert.Equal("ls", PermissionRuleMatcher.ExtractSubject(element));
        Assert.Null(PermissionRuleMatcher.ExtractSubject(JsonSerializer.SerializeToElement(new { command = 5 })));
    }
}
