namespace Alcoler.Terminal;

public readonly record struct TerminalColor(byte R, byte G, byte B)
{
    public static TerminalColor FromRgb(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

/// <summary>Console-style ANSI palette; index 0-15 basic, 16-255 xterm extension.</summary>
public static class TerminalPalette
{
    public static readonly TerminalColor[] Basic =
    [
        new(0x00, 0x00, 0x00),
        new(0xAA, 0x00, 0x00),
        new(0x00, 0xAA, 0x00),
        new(0xAA, 0x55, 0x00),
        new(0x00, 0x00, 0xAA),
        new(0xAA, 0x00, 0xAA),
        new(0x00, 0xAA, 0xAA),
        new(0xAA, 0xAA, 0xAA),
        new(0x55, 0x55, 0x55),
        new(0xFF, 0x55, 0x55),
        new(0x55, 0xFF, 0x55),
        new(0xFF, 0xFF, 0x55),
        new(0x55, 0x55, 0xFF),
        new(0xFF, 0x55, 0xFF),
        new(0x55, 0xFF, 0xFF),
        new(0xFF, 0xFF, 0xFF),
    ];

    private static readonly int[] CubeSteps = [0x00, 0x5F, 0x87, 0xAF, 0xD7, 0xFF];

    public static TerminalColor Indexed(int index)
    {
        if (index is >= 0 and < 16)
        {
            return Basic[index];
        }
        if (index is >= 16 and < 232)
        {
            var cube = index - 16;
            return new TerminalColor(
                (byte)CubeSteps[cube / 36],
                (byte)CubeSteps[cube / 6 % 6],
                (byte)CubeSteps[cube % 6]);
        }
        if (index is >= 232 and < 256)
        {
            var gray = 8 + (index - 232) * 10;
            return new TerminalColor((byte)gray, (byte)gray, (byte)gray);
        }
        return Basic[7];
    }
}

/// <summary>
/// Screen model of Cols x Rows cells plus frozen scrollback lines. Not thread
/// safe: all mutations happen on the UI thread, one token batch at a time.
/// </summary>
public sealed class TerminalBuffer
{
    public const int MaxScrollbackLines = 10_000;

    private const int TabWidth = 8;

    public readonly record struct Cell(char Char, TerminalColor? Foreground);

    private Cell[][] _grid;
    private readonly List<Cell[]> _scrollback = [];

    private TerminalColor? _foreground;
    private bool _bold;
    private bool _wrapPending;

    public TerminalBuffer(int cols, int rows)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        _grid = NewGrid(Cols, Rows);
    }

    public int Cols { get; private set; }

    public int Rows { get; private set; }

    public int CursorCol { get; private set; }

    public int CursorRow { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    public IReadOnlyList<Cell[]> Scrollback => _scrollback;

    public static Cell BlankCell => new(' ', null);

    public Cell[] GetRow(int screenRow) => _grid[screenRow];

    public void Apply(VtToken token)
    {
        switch (token)
        {
            case VtText text:
                WriteText(text.Text);
                break;
            case VtControl control:
                ApplyControl(control.Control);
                break;
            case VtCsi csi:
                ApplyCsi(csi);
                break;
        }
        // VtEscape and VtOsc are parsed upstream but intentionally ignored here.
    }

    public void ApplyTokens(IEnumerable<VtToken> tokens)
    {
        foreach (var token in tokens)
        {
            Apply(token);
        }
    }

    public void WriteText(string text)
    {
        foreach (var c in text)
        {
            PutChar(c);
        }
    }

    public void ApplyControl(char control)
    {
        switch (control)
        {
            case '\r':
                CursorCol = 0;
                _wrapPending = false;
                break;
            case '\n' or '\v' or '\f':
                LineFeed();
                break;
            case '\b':
                if (_wrapPending)
                {
                    _wrapPending = false;
                }
                else
                {
                    CursorCol = Math.Max(0, CursorCol - 1);
                }
                break;
            case '\t':
                CursorCol = Math.Min(Cols - 1, CursorCol / TabWidth * TabWidth + TabWidth);
                break;
        }
    }

    public void LineFeed()
    {
        _wrapPending = false;
        if (CursorRow == Rows - 1)
        {
            ScrollUp();
        }
        else
        {
            CursorRow++;
        }
    }

    public void MoveCursorTo(int row, int col)
    {
        // CUP parameters are 1-based.
        CursorRow = Math.Clamp(row - 1, 0, Rows - 1);
        CursorCol = Math.Clamp(col - 1, 0, Cols - 1);
        _wrapPending = false;
    }

    public void MoveCursorBy(int rowDelta, int colDelta)
    {
        CursorRow = Math.Clamp(CursorRow + rowDelta, 0, Rows - 1);
        CursorCol = Math.Clamp(CursorCol + colDelta, 0, Cols - 1);
        _wrapPending = false;
    }

    public void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var row = CursorRow + 1; row < Rows; row++)
                {
                    ClearRow(row);
                }
                break;
            case 1:
                EraseLine(1);
                for (var row = 0; row < CursorRow; row++)
                {
                    ClearRow(row);
                }
                break;
            case 2 or 3:
                for (var row = 0; row < Rows; row++)
                {
                    ClearRow(row);
                }
                break;
        }
    }

    public void EraseLine(int mode)
    {
        var row = _grid[CursorRow];
        int start;
        int end;
        switch (mode)
        {
            case 0:
                start = CursorCol;
                end = Cols - 1;
                break;
            case 1:
                start = 0;
                end = CursorCol;
                break;
            default:
                start = 0;
                end = Cols - 1;
                break;
        }
        for (var col = start; col <= end; col++)
        {
            row[col] = BlankCell;
        }
    }

    public void Clear()
    {
        _grid = NewGrid(Cols, Rows);
        _scrollback.Clear();
        CursorRow = 0;
        CursorCol = 0;
        _wrapPending = false;
        _foreground = null;
        _bold = false;
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows)
        {
            return;
        }

        // No reflow: content stays row-anchored; rows pushed off the top are frozen
        // into scrollback so the cursor remains visible.
        var rowOffset = rows < Rows ? Math.Max(0, CursorRow - (rows - 1)) : 0;
        var grid = NewGrid(cols, rows);
        for (var row = 0; row < Rows; row++)
        {
            if (row < rowOffset)
            {
                _scrollback.Add(_grid[row]);
                TrimScrollback();
                continue;
            }
            var target = row - rowOffset;
            if (target >= rows)
            {
                break;
            }
            Array.Copy(_grid[row], grid[target], Math.Min(Cols, cols));
        }

        _grid = grid;
        Cols = cols;
        Rows = rows;
        CursorRow = Math.Clamp(CursorRow - rowOffset, 0, rows - 1);
        CursorCol = Math.Min(CursorCol, cols - 1);
        _wrapPending = false;
    }

    public string GetRowText(int screenRow) => RowText(_grid[screenRow]);

    public string GetScrollbackText(int index) => RowText(_scrollback[index]);

    private static string RowText(Cell[] row) => new string(row.Select(c => c.Char).ToArray()).TrimEnd();

    private void PutChar(char c)
    {
        if (_wrapPending)
        {
            CursorCol = 0;
            _wrapPending = false;
            AdvanceRow();
        }
        _grid[CursorRow][CursorCol] = new Cell(c, _foreground);
        if (CursorCol == Cols - 1)
        {
            // Deferred wrap: xterm keeps the cursor on the last column until the
            // next printable character, so full-width lines do not scroll early.
            _wrapPending = true;
        }
        else
        {
            CursorCol++;
        }
    }

    private void AdvanceRow()
    {
        if (CursorRow == Rows - 1)
        {
            ScrollUp();
        }
        else
        {
            CursorRow++;
        }
    }

    private void ScrollUp()
    {
        _scrollback.Add(_grid[0]);
        TrimScrollback();
        for (var row = 1; row < Rows; row++)
        {
            _grid[row - 1] = _grid[row];
        }
        _grid[Rows - 1] = NewRow(Cols);
    }

    private void TrimScrollback()
    {
        if (_scrollback.Count > MaxScrollbackLines)
        {
            _scrollback.RemoveAt(0);
        }
    }

    private void ClearRow(int row) => Array.Fill(_grid[row], BlankCell);

    private static Cell[][] NewGrid(int cols, int rows) =>
        Enumerable.Range(0, rows).Select(_ => NewRow(cols)).ToArray();

    private static Cell[] NewRow(int cols)
    {
        var row = new Cell[cols];
        Array.Fill(row, BlankCell);
        return row;
    }

    private void ApplyCsi(VtCsi csi)
    {
        switch (csi.Final)
        {
            case 'H' or 'f':
                MoveCursorTo(csi.Param(0, 1), csi.Param(1, 1));
                break;
            case 'A':
                MoveCursorBy(-csi.Param(0, 1), 0);
                break;
            case 'B':
                MoveCursorBy(csi.Param(0, 1), 0);
                break;
            case 'C':
                MoveCursorBy(0, csi.Param(0, 1));
                break;
            case 'D':
                MoveCursorBy(0, -csi.Param(0, 1));
                break;
            case 'G':
                CursorCol = Math.Clamp(csi.Param(0, 1) - 1, 0, Cols - 1);
                _wrapPending = false;
                break;
            case 'd':
                CursorRow = Math.Clamp(csi.Param(0, 1) - 1, 0, Rows - 1);
                _wrapPending = false;
                break;
            case 'J':
                EraseDisplay(csi.Param(0, 0));
                break;
            case 'K':
                EraseLine(csi.Param(0, 0));
                break;
            case 'm':
                ApplySgr(csi.Parameters);
                break;
            case 'h' when csi.Private && csi.Parameters is [25]:
                CursorVisible = true;
                break;
            case 'l' when csi.Private && csi.Parameters is [25]:
                CursorVisible = false;
                break;
        }
    }

    private void ApplySgr(IReadOnlyList<int> parameters)
    {
        if (parameters is [-1] or [])
        {
            _foreground = null;
            _bold = false;
            return;
        }

        for (var i = 0; i < parameters.Count; i++)
        {
            var code = Math.Max(0, parameters[i]);
            switch (code)
            {
                case 0:
                    _foreground = null;
                    _bold = false;
                    break;
                case 1:
                    _bold = true;
                    break;
                case 22:
                    _bold = false;
                    break;
                case 39:
                    _foreground = null;
                    break;
                case >= 30 and <= 37:
                    _foreground = TerminalPalette.Indexed(code - 30 + (_bold ? 8 : 0));
                    break;
                case >= 90 and <= 97:
                    _foreground = TerminalPalette.Indexed(code - 90 + 8);
                    break;
                case >= 40 and <= 47 or 49 or >= 100 and <= 107:
                    // Background colors are consumed but not stored; cells track foreground only.
                    break;
                case 38 when i + 1 < parameters.Count && parameters[i + 1] == 5 && i + 2 < parameters.Count:
                    _foreground = TerminalPalette.Indexed(Math.Max(0, parameters[i + 2]));
                    i += 2;
                    break;
                case 38 when i + 1 < parameters.Count && parameters[i + 1] == 2 && i + 4 < parameters.Count:
                    _foreground = new TerminalColor(
                        (byte)Math.Clamp(Math.Max(0, parameters[i + 2]), 0, 255),
                        (byte)Math.Clamp(Math.Max(0, parameters[i + 3]), 0, 255),
                        (byte)Math.Clamp(Math.Max(0, parameters[i + 4]), 0, 255));
                    i += 4;
                    break;
            }
        }
    }
}
