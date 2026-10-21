using System.Security.Cryptography;
using System.Text;
using Alcoler.Kernel.Context;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class SummarizationPromptTests
{
    [Fact]
    public void Prompt_MatchesReferenceIncludingTagDisciplineAndSecurityConstraints()
    {
        var prompt = SummarizationPrompt.BuildSummaryUserPrompt();

        Assert.StartsWith(SummarizationPrompt.TextOnlyPreamble, prompt);
        Assert.EndsWith(SummarizationPrompt.TextOnlyTrailer, prompt);
        Assert.Contains("an <analysis> block followed by a <summary> block", prompt);
        Assert.Contains("These MUST be preserved verbatim in the summary", prompt);
        Assert.DoesNotContain("quoted VERBATIM", prompt);
        Assert.DoesNotContain("<conversation-transcript>", prompt);
        Assert.Equal("55BF8287D1F1E9D1130D32718432CBFAB61EC0E47812E1A5CFD1736EB924F64F", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))));
    }

    [Fact]
    public void Prompt_ContainsAllNineNumberedSections()
    {
        var prompt = SummarizationPrompt.BuildSummaryUserPrompt();
        string[] headings =
        [
            "Primary Request and Intent", "Key Technical Concepts", "Files and Code Sections",
            "Errors and fixes", "Problem Solving", "All user messages", "Pending Tasks", "Current Work", "Optional Next Step",
        ];
        for (var i = 0; i < headings.Length; i++)
            Assert.Contains($"{i + 1}. {headings[i]}:", prompt);
    }

    [Fact]
    public void Prompt_AppendsUntrimmedManualInstructionsBeforeTrailer()
    {
        const string instructions = "  Focus on the database migration work.\nPreserve secrets constraints.  ";
        var prompt = SummarizationPrompt.BuildSummaryUserPrompt(instructions);

        Assert.EndsWith("\n\nAdditional Instructions:\n" + instructions + SummarizationPrompt.TextOnlyTrailer, prompt);
        Assert.Equal(SummarizationPrompt.BuildSummaryUserPrompt(), SummarizationPrompt.BuildSummaryUserPrompt(" \n\t"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("  \n ", "")]
    [InlineData("<analysis>only analysis</analysis>", "")]
    [InlineData("<analysis>private\nanalysis</analysis>\n\n<summary>\nkeep\n\n\nthis\n</summary>", "Summary:\nkeep\n\nthis")]
    [InlineData("intro\n<analysis>discard</analysis>\n<summary>content</summary>\nend", "intro\n\nSummary:\ncontent\nend")]
    [InlineData("  Plain summary\n\n\nMore  ", "Plain summary\n\nMore")]
    [InlineData("<SUMMARY>unchanged</SUMMARY>", "<SUMMARY>unchanged</SUMMARY>")]
    [InlineData("<summary>first</summary><summary>second</summary>", "Summary:\nfirst<summary>second</summary>")]
    [InlineData("<analysis>first</analysis><analysis>second</analysis><summary>body</summary>", "<analysis>second</analysis>Summary:\nbody")]
    public void FormatSummary_MatchesReference(string? input, string expected)
    {
        Assert.Equal(expected, SummarizationPrompt.FormatSummary(input));
    }
}
