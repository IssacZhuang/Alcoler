using Alcoler.Terminal;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Tests.Terminal;

public class TerminalBufferTests
{
    private static TerminalBuffer Feed(string input, int cols = 20, int rows = 5)
    {
        var buffer = new TerminalBuffer(cols, rows);
        buffer.ApplyTokens(new VtParser().Feed(input));
        return buffer;
    }

    [Fact]
    public void WriteText_PlacesCharacters_AndAdvancesCursor()
    {
        var buffer = Feed("hi");
        Assert.Equal("hi", buffer.GetRowText(0));
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(2, buffer.CursorCol);
    }

    [Fact]
    public void CarriageReturnAndLineFeed_StartNewRow()
    {
        var buffer = Feed("first\r\nsecond");
        Assert.Equal("first", buffer.GetRowText(0));
        Assert.Equal("second", buffer.GetRowText(1));
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(6, buffer.CursorCol);
    }

    [Fact]
    public void LineFeed_Alone_DoesNotReturnCarriage()
    {
        var buffer = Feed("abc\ndef");
        Assert.Equal("abc", buffer.GetRowText(0));
        Assert.Equal("   def", buffer.GetRowText(1));
    }

    [Fact]
    public void Backspace_MovesCursorBack_WithoutErasing()
    {
        var buffer = new TerminalBuffer(20, 5);
        buffer.ApplyTokens(new VtParser().Feed("abc\b\bZ"));
        // Backspace only moves the cursor; 'Z' overwrites 'b' and 'c' survives.
        Assert.Equal("aZc", buffer.GetRowText(0));
        Assert.Equal(2, buffer.CursorCol);
    }

    [Fact]
    public void PlainBackspace_OnlyMovesCursor()
    {
        var buffer = Feed("abc\b");
        Assert.Equal("abc", buffer.GetRowText(0));
        Assert.Equal(2, buffer.CursorCol);
    }

    [Fact]
    public void Tab_AdvancesToNextMultipleOfEight()
    {
        var buffer = Feed("a\tb");
        Assert.Equal("a       b", buffer.GetRowText(0));
        Assert.Equal(9, buffer.CursorCol);
    }

    [Fact]
    public void Wrap_AtLastColumn_ContinuesOnNextRow()
    {
        var buffer = new TerminalBuffer(5, 3);
        buffer.WriteText("abcdef");
        Assert.Equal("abcde", buffer.GetRowText(0));
        Assert.Equal("f", buffer.GetRowText(1));
        Assert.Equal(1, buffer.CursorCol);
        Assert.Equal(1, buffer.CursorRow);
    }

    [Fact]
    public void Wrap_IsDeferred_UntilNextPrintable()
    {
        var buffer = new TerminalBuffer(5, 3);
        buffer.WriteText("abcde");
        Assert.Equal(4, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
        buffer.ApplyControl('\r');
        buffer.ApplyControl('\n');
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
        Assert.Equal("abcde", buffer.GetRowText(0));
    }

    [Fact]
    public void Scrollback_Grows_WhenRowsExceed()
    {
        var buffer = new TerminalBuffer(10, 2);
        buffer.ApplyTokens(new VtParser().Feed("one\r\ntwo\r\nthree"));
        Assert.Single(buffer.Scrollback);
        Assert.Equal("one", buffer.GetScrollbackText(0));
        Assert.Equal("two", buffer.GetRowText(0));
        Assert.Equal("three", buffer.GetRowText(1));
        Assert.Equal(1, buffer.CursorRow);
    }

    [Fact]
    public void Scrollback_TrimsToCap()
    {
        var buffer = new TerminalBuffer(10, 1);
        for (var i = 0; i < TerminalBuffer.MaxScrollbackLines + 5; i++)
        {
            buffer.WriteText(i.ToString());
            buffer.ApplyControl('\r');
            buffer.ApplyControl('\n');
        }
        Assert.Equal(TerminalBuffer.MaxScrollbackLines, buffer.Scrollback.Count);
    }

    [Fact]
    public void EraseDisplay_All_ClearsScreenButKeepsCursor()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.ApplyTokens(new VtParser().Feed("aa\r\nbb\r\ncc\x1b[2;2H\x1b[2J"));
        Assert.Equal("", buffer.GetRowText(0));
        Assert.Equal("", buffer.GetRowText(1));
        Assert.Equal("", buffer.GetRowText(2));
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(1, buffer.CursorCol);
    }

    [Fact]
    public void EraseDisplay_FromCursor_ClearsToTheEnd()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.ApplyTokens(new VtParser().Feed("aa\r\nbb\r\ncc\x1b[2;2H\x1b[J"));
        Assert.Equal("aa", buffer.GetRowText(0));
        Assert.Equal("b", buffer.GetRowText(1));
        Assert.Equal("", buffer.GetRowText(2));
    }

    [Fact]
    public void EraseLine_ToEnd_ClearsOnlyCurrentRow()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.ApplyTokens(new VtParser().Feed("aa\r\nbb\r\ncc\x1b[2;2H\x1b[K"));
        Assert.Equal("aa", buffer.GetRowText(0));
        Assert.Equal("b", buffer.GetRowText(1));
        Assert.Equal("cc", buffer.GetRowText(2));
    }

    [Fact]
    public void Cup_MovesCursor()
    {
        var buffer = new TerminalBuffer(10, 5);
        buffer.ApplyTokens(new VtParser().Feed("\x1b[3;4Hx"));
        Assert.Equal("   x", buffer.GetRowText(2));
        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(4, buffer.CursorCol);
    }

    [Fact]
    public void RelativeMoves_ClampAtEdges()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.ApplyTokens(new VtParser().Feed("\x1b[5;5H\x1b[2A\x1b[10D"));
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
        buffer.ApplyTokens(new VtParser().Feed("\x1b[9B\x1b[9C"));
        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(9, buffer.CursorCol);
    }

    [Fact]
    public void Sgr_BasicColors_AndReset()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.ApplyTokens(new VtParser().Feed("\x1b[31mR\x1b[32mG\x1b[0mP"));
        Assert.Equal(TerminalPalette.Basic[1], buffer.GetRow(0)[0].Foreground);
        Assert.Equal(TerminalPalette.Basic[2], buffer.GetRow(0)[1].Foreground);
        Assert.Null(buffer.GetRow(0)[2].Foreground);
    }

    [Fact]
    public void Sgr_BrightColors_90to97()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.ApplyTokens(new VtParser().Feed("\x1b[91mA\x1b[97mB"));
        Assert.Equal(TerminalPalette.Basic[9], buffer.GetRow(0)[0].Foreground);
        Assert.Equal(TerminalPalette.Basic[15], buffer.GetRow(0)[1].Foreground);
    }

    [Fact]
    public void Sgr_Bold_PromotesBasicToBright()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.ApplyTokens(new VtParser().Feed("\x1b[1;31mA\x1b[22;31mB"));
        Assert.Equal(TerminalPalette.Basic[9], buffer.GetRow(0)[0].Foreground);
        Assert.Equal(TerminalPalette.Basic[1], buffer.GetRow(0)[1].Foreground);
    }

    [Fact]
    public void Sgr_IndexedAndTrueColor()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.ApplyTokens(new VtParser().Feed("\x1b[38;5;196mA\x1b[38;2;10;20;30mB"));
        Assert.Equal(TerminalPalette.Indexed(196), buffer.GetRow(0)[0].Foreground);
        Assert.Equal(new TerminalColor(10, 20, 30), buffer.GetRow(0)[1].Foreground);
    }

    [Fact]
    public void Sgr_DefaultForeground_39()
    {
        var buffer = new TerminalBuffer(10, 3);
        buffer.ApplyTokens(new VtParser().Feed("\x1b[33mA\x1b[39mB"));
        Assert.Equal(TerminalPalette.Basic[3], buffer.GetRow(0)[0].Foreground);
        Assert.Null(buffer.GetRow(0)[1].Foreground);
    }

    [Fact]
    public void Sgr_ColorsPersist_AcrossTextWithinSameSequence()
    {
        var buffer = Feed("\x1b[36mabc");
        for (var col = 0; col < 3; col++)
        {
            Assert.Equal(TerminalPalette.Basic[6], buffer.GetRow(0)[col].Foreground);
        }
    }

    [Fact]
    public void Clear_ResetsScreenAndScrollback()
    {
        var buffer = new TerminalBuffer(10, 2);
        buffer.ApplyTokens(new VtParser().Feed("one\r\ntwo\r\nthree\x1b[31m"));
        buffer.Clear();
        Assert.Empty(buffer.Scrollback);
        Assert.Equal("", buffer.GetRowText(0));
        Assert.Equal("", buffer.GetRowText(1));
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
        buffer.WriteText("x");
        Assert.Null(buffer.GetRow(0)[0].Foreground);
    }

    [Fact]
    public void Clear_ReleasesFrozenRowsAfterScrollbackWraps()
    {
        var buffer = new TerminalBuffer(10, 1);
        var frozenRows = PopulateScrollback(buffer);

        buffer.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Empty(buffer.Scrollback);
        Assert.Equal(0, frozenRows.Count(row => row.IsAlive));
        buffer.WriteText("new");
        buffer.LineFeed();
        Assert.Single(buffer.Scrollback);
        Assert.Equal("new", buffer.GetScrollbackText(0));
        GC.KeepAlive(buffer);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] PopulateScrollback(TerminalBuffer buffer)
    {
        for (var index = 0; index < TerminalBuffer.MaxScrollbackLines + 5; index++)
        {
            buffer.WriteText(index.ToString());
            buffer.ApplyControl('\r');
            buffer.LineFeed();
        }
        Assert.Equal("5", buffer.GetScrollbackText(0));
        Assert.Equal((TerminalBuffer.MaxScrollbackLines + 4).ToString(), buffer.GetScrollbackText(TerminalBuffer.MaxScrollbackLines - 1));
        return buffer.Scrollback.Select(row => new WeakReference(row)).ToArray();
    }

    [Fact]
    public void Resize_ShrinkingRows_MovesTopLinesToScrollback()
    {
        var buffer = new TerminalBuffer(10, 4);
        buffer.ApplyTokens(new VtParser().Feed("a\r\nb\r\nc\r\nd"));
        Assert.Equal(3, buffer.CursorRow);
        buffer.Resize(10, 2);
        Assert.Equal(2, buffer.Scrollback.Count);
        Assert.Equal("a", buffer.GetScrollbackText(0));
        Assert.Equal("b", buffer.GetScrollbackText(1));
        Assert.Equal("c", buffer.GetRowText(0));
        Assert.Equal("d", buffer.GetRowText(1));
        Assert.Equal(1, buffer.CursorRow);
    }

    [Fact]
    public void Resize_GrowingRows_PadsWithBlankRows()
    {
        var buffer = new TerminalBuffer(10, 2);
        buffer.ApplyTokens(new VtParser().Feed("a\r\nb"));
        buffer.Resize(12, 4);
        Assert.Equal(4, buffer.Rows);
        Assert.Equal(12, buffer.Cols);
        Assert.Equal("a", buffer.GetRowText(0));
        Assert.Equal("b", buffer.GetRowText(1));
        Assert.Equal("", buffer.GetRowText(2));
        Assert.Equal(1, buffer.CursorRow);
    }

    [Fact]
    public void Osc_IsIgnored_AndDoesNotDisturbOutput()
    {
        var buffer = new TerminalBuffer(20, 3);
        buffer.ApplyTokens(new VtParser().Feed("\x1b]0;window title\x07ok"));
        Assert.Equal("ok", buffer.GetRowText(0));
    }

    [Fact]
    public void FullPipeline_RenderedAcrossChunks()
    {
        var parser = new VtParser();
        var buffer = new TerminalBuffer(20, 5);
        foreach (var chunk in new[] { "\x1b[32m$", " \r\n", "done", "\x1b[0m" })
        {
            buffer.ApplyTokens(parser.Feed(chunk));
        }
        Assert.Equal("$", buffer.GetRowText(0));
        Assert.Equal("done", buffer.GetRowText(1));
        Assert.Equal(TerminalPalette.Basic[2], buffer.GetRow(0)[0].Foreground);
        Assert.Equal(TerminalPalette.Basic[2], buffer.GetRow(1)[0].Foreground);
    }
}
