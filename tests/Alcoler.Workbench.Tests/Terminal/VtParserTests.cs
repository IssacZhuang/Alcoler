using Alcoler.Workbench.Terminal;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Workbench.Tests.Terminal;

public class VtParserTests
{
    [Fact]
    public void PlainText_EmitsSingleRun()
    {
        var tokens = new VtParser().Feed("hello world");
        var text = Assert.IsType<VtText>(Assert.Single(tokens));
        Assert.Equal("hello world", text.Text);
    }

    [Fact]
    public void C0Controls_EmitControlTokens()
    {
        // \b is used instead of \x08 because \x is variable-length and would swallow 'd'.
        var tokens = new VtParser().Feed("a\r\nb\tc\bd");
        Assert.Equal(
            [
                new VtText("a"),
                new VtControl('\r'),
                new VtControl('\n'),
                new VtText("b"),
                new VtControl('\t'),
                new VtText("c"),
                new VtControl('\x08'),
                new VtText("d"),
            ],
            tokens);
    }

    [Fact]
    public void Sgr_ParsesFinalByteAndParameters()
    {
        var tokens = new VtParser().Feed("\x1b[1;31m");
        var csi = Assert.IsType<VtCsi>(Assert.Single(tokens));
        Assert.Equal('m', csi.Final);
        Assert.False(csi.Private);
        Assert.Equal([1, 31], csi.Parameters);
    }

    [Fact]
    public void Sgr_WithNoParameters_ReportsOmittedParameter()
    {
        var tokens = new VtParser().Feed("\x1b[m");
        var csi = Assert.IsType<VtCsi>(Assert.Single(tokens));
        Assert.Equal('m', csi.Final);
        Assert.Equal([-1], csi.Parameters);
        Assert.Equal(0, csi.Param(0, 0));
    }

    [Fact]
    public void Cup_ParsesRowAndColumn()
    {
        var tokens = new VtParser().Feed("\x1b[10;20H");
        var csi = Assert.IsType<VtCsi>(Assert.Single(tokens));
        Assert.Equal('H', csi.Final);
        Assert.Equal([10, 20], csi.Parameters);
        Assert.Equal(10, csi.Param(0, 1));
        Assert.Equal(20, csi.Param(1, 1));
    }

    [Fact]
    public void Cup_WithOmittedParameters_UsesDefaults()
    {
        var csi = Assert.IsType<VtCsi>(Assert.Single(new VtParser().Feed("\x1b[;5H")));
        Assert.Equal([-1, 5], csi.Parameters);
    }

    [Fact]
    public void PrivateMarker_IsReported()
    {
        var csi = Assert.IsType<VtCsi>(Assert.Single(new VtParser().Feed("\x1b[?25l")));
        Assert.True(csi.Private);
        Assert.Equal([25], csi.Parameters);
        Assert.Equal('l', csi.Final);
    }

    [Fact]
    public void PartialEscape_AcrossChunkBoundary_CompletesOnNextChunk()
    {
        var parser = new VtParser();
        // Completed text flushes at chunk end; the trailing escape stays pending.
        Assert.Equal([new VtText("ok")], parser.Feed("ok\x1b[3"));
        var tokens = parser.Feed("1m");
        var csi = Assert.IsType<VtCsi>(Assert.Single(tokens));
        Assert.Equal('m', csi.Final);
        Assert.Equal([31], csi.Parameters);
    }

    [Fact]
    public void PartialEscape_CharacterByCharacter_FeedsStillParse()
    {
        var parser = new VtParser();
        IReadOnlyList<VtToken> last = [];
        foreach (var c in "\x1b[2J")
        {
            last = parser.Feed([c]);
            if (c != 'J')
            {
                Assert.Empty(last);
            }
        }
        var csi = Assert.IsType<VtCsi>(Assert.Single(last));
        Assert.Equal('J', csi.Final);
        Assert.Equal([2], csi.Parameters);
    }

    [Fact]
    public void PartialOsc_AcrossChunkBoundary_TerminatesOnBel()
    {
        var parser = new VtParser();
        Assert.Empty(parser.Feed("\x1b]0;My Ti"));
        var tokens = parser.Feed("tle\x07more");
        Assert.Equal(2, tokens.Count);
        var osc = Assert.IsType<VtOsc>(tokens[0]);
        Assert.Equal("0;My Title", osc.Payload);
        Assert.Equal("more", Assert.IsType<VtText>(tokens[1]).Text);
    }

    [Fact]
    public void Osc_WithStringTerminator_IsParsed()
    {
        var parser = new VtParser();
        var tokens = parser.Feed("\x1b]2;title\x1b\\x");
        var osc = Assert.IsType<VtOsc>(tokens[0]);
        Assert.Equal("2;title", osc.Payload);
        Assert.Equal("x", Assert.IsType<VtText>(tokens[1]).Text);
    }

    [Fact]
    public void TwoCharacterEscape_EmitsEscapeToken()
    {
        // \u001b is fixed-width; "\x1b7" would be a single U+1B7A character.
        var tokens = new VtParser().Feed("\u001b7A");
        Assert.Equal([new VtEscape('7'), new VtText("A")], tokens);
    }

    [Fact]
    public void CharsetDesignator_IsConsumedSilently()
    {
        var tokens = new VtParser().Feed("\x1b(Btext");
        var text = Assert.IsType<VtText>(Assert.Single(tokens));
        Assert.Equal("text", text.Text);
    }

    [Fact]
    public void CsiWithIntermediate_IsDropped()
    {
        // ESC[ q (DECSCUSR) carries an intermediate space and must not surface as a cursor move.
        var tokens = new VtParser().Feed("\x1b[ qz");
        Assert.Equal([new VtText("z")], tokens);
    }

    [Fact]
    public void MixedStream_TokenizesInOrder()
    {
        var parser = new VtParser();
        var tokens = parser.Feed("AB\x1b[31mCD\r\x1b[2K\x1b[H");
        Assert.Equal(
            [
                new VtText("AB"),
                new VtCsi('m', [31], false),
                new VtText("CD"),
                new VtControl('\r'),
                new VtCsi('K', [2], false),
                new VtCsi('H', [-1], false),
            ],
            tokens);
    }
}
