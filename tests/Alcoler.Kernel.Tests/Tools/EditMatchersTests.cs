using Alcoler.Adapters.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class EditMatchersTests
{
    [Fact]
    public void Exact_UniqueMatch_ReturnsActualString()
    {
        var match = EditMatchers.FindEditMatch("alpha beta gamma", "beta", replaceAll: false);

        var matched = Assert.IsType<EditMatch.Matched>(match);
        Assert.Equal("beta", matched.ActualString);
        Assert.Equal(EditMatchStrategy.Exact, matched.Strategy);
        Assert.Equal(1, matched.CandidateCount);
    }

    [Fact]
    public void Exact_RepeatedSameValue_IsMatchedWithCandidateCount()
    {
        var match = EditMatchers.FindEditMatch("beta and beta", "beta", replaceAll: false);

        var matched = Assert.IsType<EditMatch.Matched>(match);
        Assert.Equal(2, matched.CandidateCount);
    }

    [Fact]
    public void AbsentEverywhere_ReturnsNotFound()
    {
        var match = EditMatchers.FindEditMatch("alpha\ngamma", "beta", replaceAll: false);

        Assert.IsType<EditMatch.NotFound>(match);
    }

    [Fact]
    public void QuoteNormalized_MatchesCurlyQuotesInContent()
    {
        var match = EditMatchers.FindEditMatch("say \u201Chello\u201D now", "say \"hello\" now", replaceAll: false);

        var matched = Assert.IsType<EditMatch.Matched>(match);
        Assert.Equal(EditMatchStrategy.QuoteNormalized, matched.Strategy);
        Assert.Equal("say \u201Chello\u201D now", matched.ActualString);
    }

    [Fact]
    public void LineNumberPrefix_ColonAndTabForms_AreStripped()
    {
        var colon = EditMatchers.FindEditMatch("alpha\nbeta\ngamma", "12: gamma", replaceAll: false);
        var tab = EditMatchers.FindEditMatch("alpha\nbeta\ngamma", "12\tbeta", replaceAll: false);

        Assert.Equal("gamma", Assert.IsType<EditMatch.Matched>(colon).ActualString);
        Assert.Equal("beta", Assert.IsType<EditMatch.Matched>(tab).ActualString);
    }

    [Fact]
    public void LineNumberPrefix_UnprefixedLine_FallsThrough()
    {
        var match = EditMatchers.FindEditMatch("alpha\nbeta", "12: alpha\nplain beta", replaceAll: false);

        Assert.IsType<EditMatch.NotFound>(match);
    }

    [Fact]
    public void EscapeNormalized_UnescapesVisibleCharacters()
    {
        var match = EditMatchers.FindEditMatch("line1\nline2", "line1\\nline2", replaceAll: false);

        var matched = Assert.IsType<EditMatch.Matched>(match);
        Assert.Equal(EditMatchStrategy.EscapeNormalized, matched.Strategy);
        Assert.Equal("line1\nline2", matched.ActualString);
    }

    [Fact]
    public void NormalizeReplacementForMatch_EscapeNormalized_UnescapesNewString()
    {
        var replacement = EditMatchers.NormalizeReplacementForMatch(EditMatchStrategy.EscapeNormalized, "a\\tb");

        Assert.Equal("a\tb", replacement);
    }

    [Fact]
    public void UnicodeEscapeNormalized_MatchesRealCharacter()
    {
        var match = EditMatchers.FindEditMatch("caf\u00e9 cr\u00e8me", "caf\\u00e9 cr\\u00e8me", replaceAll: false);

        var matched = Assert.IsType<EditMatch.Matched>(match);
        Assert.Equal(EditMatchStrategy.UnicodeEscapeNormalized, matched.Strategy);
        Assert.Equal("caf\u00e9 cr\u00e8me", matched.ActualString);
    }

    [Fact]
    public void LineTrimmed_MatchesContentWithSurroundingWhitespace()
    {
        var content = "start\n  alpha  \n\tbeta\nend";
        var match = EditMatchers.FindEditMatch(content, "alpha\nbeta", replaceAll: false);

        var matched = Assert.IsType<EditMatch.Matched>(match);
        Assert.Equal(EditMatchStrategy.LineTrimmed, matched.Strategy);
        Assert.Equal("  alpha  \n\tbeta", matched.ActualString);
    }

    [Fact]
    public void IndentationFlexible_MatchesShiftedBlock_Directly()
    {
        var content = "void f()\n{\n    if (x)\n    {\n        return;\n    }\n}";
        var search = "if (x)\n{\n    return;\n}";

        var candidates = EditMatchers.CollectCandidates(EditMatchStrategy.IndentationFlexible, content, search);

        var value = Assert.Single(candidates);
        Assert.Equal("    if (x)\n    {\n        return;\n    }", value);
    }

    [Fact]
    public void IndentationFlexible_UniformIndentShiftOnly_DoesNotMatchDissimilarBlock()
    {
        var content = "if (x)\n{\n    return;\n}";
        var candidates = EditMatchers.CollectCandidates(
            EditMatchStrategy.IndentationFlexible, content, "if (y)\n{\n    return;\n}");

        Assert.Empty(candidates);
    }

    [Fact]
    public void BlockAnchor_AnchorsFirstAndLastLine_AllowsSimilarMiddles()
    {
        var content = "function main() {\n  const a = 1;\n  const b = 2;\n  return a + b;\n}";
        var search = "function main() {\n  const a = 1;\n  const q = 2;\n  return a + b;\n}";

        var match = EditMatchers.FindEditMatch(content, search, replaceAll: false);

        var matched = Assert.IsType<EditMatch.Matched>(match);
        Assert.Equal(EditMatchStrategy.BlockAnchor, matched.Strategy);
        Assert.Equal(content, matched.ActualString);
    }

    [Fact]
    public void BlockAnchor_DissimilarMiddles_DoNotMatch()
    {
        var content = "header\ncompute something entirely different here\nfooter";
        var search = "header\nunrelated middle\nfooter";

        var match = EditMatchers.FindEditMatch(content, search, replaceAll: false);

        Assert.IsType<EditMatch.NotFound>(match);
    }

    [Fact]
    public void MultipleDistinctCandidates_ReportedAsAmbiguous()
    {
        var content = "alpha \nbeta\nalpha\t\nbeta";
        var match = EditMatchers.FindEditMatch(content, "alpha\nbeta", replaceAll: false);

        var ambiguous = Assert.IsType<EditMatch.Ambiguous>(match);
        Assert.Equal(EditMatchStrategy.LineTrimmed, ambiguous.Strategy);
        Assert.Equal(2, ambiguous.CandidateCount);
    }

    [Fact]
    public void ReplaceAll_SkipsBroadMatchers()
    {
        var match = EditMatchers.FindEditMatch("  alpha\n\tbeta", "alpha\nbeta", replaceAll: true);

        Assert.IsType<EditMatch.NotFound>(match);
    }

    [Fact]
    public void ReplaceAll_StillUsesExactAndNormalizationMatchers()
    {
        var match = EditMatchers.FindEditMatch("say \u201Chello\u201D", "say \"hello\"", replaceAll: true);

        Assert.Equal("say \u201Chello\u201D", Assert.IsType<EditMatch.Matched>(match).ActualString);
    }

    [Fact]
    public void LevenshteinDistance_ComputesKnownValues()
    {
        Assert.Equal(0, EditMatchers.LevenshteinDistance("kitten", "kitten"));
        Assert.Equal(3, EditMatchers.LevenshteinDistance("kitten", "sitting"));
        Assert.Equal(3, EditMatchers.LevenshteinDistance("", "abc"));
        Assert.Equal(3, EditMatchers.LevenshteinDistance("abc", ""));
    }

    [Fact]
    public void PreserveQuoteStyle_IdenticalOldString_KeepsNewString()
    {
        var result = EditMatchers.PreserveQuoteStyle("same", "same", "keep \"straight\"");

        Assert.Equal("keep \"straight\"", result);
    }

    [Fact]
    public void PreserveQuoteStyle_CurlyDoubleQuotes_CarriedIntoReplacement()
    {
        var result = EditMatchers.PreserveQuoteStyle(
            "say \"hello\"",
            "say \u201Chello\u201D",
            "say \"goodbye\" now");

        Assert.Equal("say \u201Cgoodbye\u201D now", result);
    }

    [Fact]
    public void PreserveQuoteStyle_CurlySingleQuotes_CarriedIntoReplacement()
    {
        var result = EditMatchers.PreserveQuoteStyle("it's", "it\u2019s", "it's fine");

        Assert.Equal("it\u2019s fine", result);
    }
}
