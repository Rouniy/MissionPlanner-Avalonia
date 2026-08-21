using System;
using System.Linq;
using System.Text;

namespace MissionPlannerAvalonia.Services;

public readonly record struct TerminalRgb(byte Red, byte Green, byte Blue);

public readonly record struct TerminalAttributes(
    TerminalRgb? Foreground = null,
    TerminalRgb? Background = null,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Inverse = false);

public readonly record struct TerminalCell(char Character, TerminalAttributes Attributes);

/// <summary>Immutable render state copied from an ANSI terminal buffer.</summary>
public sealed class TerminalSnapshot {
  private readonly TerminalCell[] _cells;

  internal TerminalSnapshot(
      int width, int height, TerminalCell[] cells,
      int cursorRow, int cursorColumn, bool cursorVisible) {
    Width = width;
    Height = height;
    _cells = cells;
    CursorRow = cursorRow;
    CursorColumn = cursorColumn;
    CursorVisible = cursorVisible;
  }

  public int Width { get; }
  public int Height { get; }
  public int CursorRow { get; }
  public int CursorColumn { get; }
  public bool CursorVisible { get; }

  public TerminalCell this[int row, int column] {
    get {
      if ((uint)row >= (uint)Height) {
        throw new ArgumentOutOfRangeException(nameof(row));
      }
      if ((uint)column >= (uint)Width) {
        throw new ArgumentOutOfRangeException(nameof(column));
      }
      return _cells[(row * Width) + column];
    }
  }
}

/// <summary>
/// Small VT100/xterm screen model used by the SSH terminal. Mission Planner's
/// WinForms SSH terminal applies these sequences directly to a RichTextBox; the
/// Avalonia port keeps the same cursor/screen semantics in a portable text model.
/// </summary>
internal sealed class AnsiTerminalBuffer {
  private static readonly TerminalRgb[] BasicColors = {
    new(0, 0, 0),
    new(255, 0, 0),
    new(0, 205, 0),
    new(255, 255, 0),
    new(92, 92, 255),
    new(255, 0, 255),
    new(0, 255, 255),
    new(255, 255, 255),
  };

  private static readonly TerminalRgb[] BrightColors = {
    new(128, 128, 128),
    new(255, 0, 0),
    new(0, 255, 0),
    new(255, 255, 0),
    new(0, 0, 255),
    new(255, 0, 255),
    new(0, 255, 255),
    new(255, 255, 255),
  };

  private static readonly TerminalRgb[] FirstSixteenPaletteColors = {
    new(0, 0, 0), new(128, 0, 0), new(0, 128, 0), new(128, 128, 0),
    new(0, 0, 128), new(128, 0, 128), new(0, 128, 128), new(192, 192, 192),
    new(128, 128, 128), new(255, 0, 0), new(0, 255, 0), new(255, 255, 0),
    new(0, 0, 255), new(255, 0, 255), new(0, 255, 255), new(255, 255, 255),
  };

  private static readonly int[] CubeLevels = { 0, 95, 135, 175, 215, 255 };

  private enum ParserState {
    Text,
    Escape,
    CharacterSet,
    Csi,
    Osc,
    OscEscape,
    StringControl,
    StringControlEscape,
  }

  private readonly int _width;
  private readonly int _height;
  private readonly StringBuilder _control = new();
  private char[,] _screen;
  private TerminalAttributes[,] _screenAttributes;
  private char[,]? _primaryScreen;
  private TerminalAttributes[,]? _primaryScreenAttributes;
  private ParserState _state;
  private int _row;
  private int _column;
  private int _savedRow;
  private int _savedColumn;
  private int _primaryRow;
  private int _primaryColumn;
  private int _scrollTop;
  private int _scrollBottom;
  private int _stringControlLength;
  private TerminalAttributes _attributes;
  private TerminalAttributes _savedAttributes;
  private TerminalAttributes _primaryAttributes;
  private bool _wrapPending;
  private bool _autoWrap = true;

  public AnsiTerminalBuffer(int width = 80, int height = 24) {
    if (width < 1) {
      throw new ArgumentOutOfRangeException(nameof(width));
    }
    if (height < 1) {
      throw new ArgumentOutOfRangeException(nameof(height));
    }

    _width = width;
    _height = height;
    _screen = NewScreen();
    _screenAttributes = NewAttributeScreen();
    _scrollBottom = height - 1;
  }

  public event Action<string>? ResponseGenerated;

  public bool ApplicationCursorKeys { get; private set; }
  public int CursorRow => _row;
  public int CursorColumn => _column;
  public int Width => _width;
  public int Height => _height;
  public bool CursorVisible { get; private set; } = true;

  public void Write(string? text) {
    if (string.IsNullOrEmpty(text)) {
      return;
    }

    foreach (char value in text) {
      Process(value);
    }
  }

  public string Render() {
    var rows = new string[_height];
    int last = Math.Max(0, _row);
    for (int row = 0; row < _height; row++) {
      var chars = new char[_width];
      for (int column = 0; column < _width; column++) {
        chars[column] = _screen[row, column];
      }
      rows[row] = new string(chars).TrimEnd();
      if (rows[row].Length > 0) {
        last = row;
      }
    }
    return string.Join('\n', rows.Take(last + 1));
  }

  public TerminalSnapshot Snapshot() {
    var cells = new TerminalCell[_width * _height];
    for (int row = 0; row < _height; row++) {
      for (int column = 0; column < _width; column++) {
        cells[(row * _width) + column] =
            new TerminalCell(_screen[row, column], _screenAttributes[row, column]);
      }
    }
    return new TerminalSnapshot(
        _width, _height, cells, _row, _column, CursorVisible);
  }

  public void Reset() {
    _attributes = default;
    _savedAttributes = default;
    _primaryAttributes = default;
    _screen = NewScreen();
    _screenAttributes = NewAttributeScreen();
    _primaryScreen = null;
    _primaryScreenAttributes = null;
    _state = ParserState.Text;
    _control.Clear();
    _stringControlLength = 0;
    _row = _column = _savedRow = _savedColumn = 0;
    _scrollTop = 0;
    _scrollBottom = _height - 1;
    _wrapPending = false;
    _autoWrap = true;
    ApplicationCursorKeys = false;
    CursorVisible = true;
  }

  private char[,] NewScreen() {
    var result = new char[_height, _width];
    Fill(result, ' ');
    return result;
  }

  private TerminalAttributes[,] NewAttributeScreen() =>
      new TerminalAttributes[_height, _width];

  private static void Fill(char[,] target, char value) {
    for (int row = 0; row < target.GetLength(0); row++) {
      for (int column = 0; column < target.GetLength(1); column++) {
        target[row, column] = value;
      }
    }
  }

  private void Process(char value) {
    switch (_state) {
      case ParserState.Text:
        ProcessText(value);
        break;
      case ParserState.Escape:
        ProcessEscape(value);
        break;
      case ParserState.CharacterSet:
        _state = ParserState.Text;
        break;
      case ParserState.Csi:
        if (value is >= '@' and <= '~') {
          ResolveCsi(_control.ToString(), value);
          _control.Clear();
          _state = ParserState.Text;
        } else if (_control.Length < 128) {
          _control.Append(value);
        } else {
          _control.Clear();
          _state = ParserState.Text;
        }
        break;
      case ParserState.Osc:
        if (value == '\a') {
          _state = ParserState.Text;
        } else if (value == '\u001b') {
          _state = ParserState.OscEscape;
        } else if (++_stringControlLength > 4096) {
          _state = ParserState.Text;
        }
        break;
      case ParserState.OscEscape:
        _state = value == '\\' ? ParserState.Text : ParserState.Osc;
        if (_state == ParserState.Osc && ++_stringControlLength > 4096) {
          _state = ParserState.Text;
        }
        break;
      case ParserState.StringControl:
        if (value == '\u001b') {
          _state = ParserState.StringControlEscape;
        } else if (++_stringControlLength > 4096) {
          _state = ParserState.Text;
        }
        break;
      case ParserState.StringControlEscape:
        _state = value == '\\' ? ParserState.Text : ParserState.StringControl;
        if (_state == ParserState.StringControl && ++_stringControlLength > 4096) {
          _state = ParserState.Text;
        }
        break;
    }
  }

  private void ProcessText(char value) {
    switch (value) {
      case '\u001b':
        _state = ParserState.Escape;
        return;
      case '\r':
        _column = 0;
        _wrapPending = false;
        return;
      case '\n':
      case '\v':
      case '\f':
        LineFeed();
        return;
      case '\b':
        _column = Math.Max(0, _column - 1);
        _wrapPending = false;
        return;
      case '\t':
        _column = Math.Min(_width - 1, ((_column / 8) + 1) * 8);
        _wrapPending = false;
        return;
      case '\0':
      case '\a':
        return;
    }

    if (char.IsControl(value)) {
      return;
    }

    if (_wrapPending) {
      _column = 0;
      LineFeed();
    }
    _screen[_row, _column] = value;
    _screenAttributes[_row, _column] = _attributes;
    if (_column == _width - 1) {
      _wrapPending = _autoWrap;
    } else {
      _column++;
    }
  }

  private void ProcessEscape(char value) {
    _state = ParserState.Text;
    switch (value) {
      case '[':
        _control.Clear();
        _state = ParserState.Csi;
        break;
      case ']':
        _stringControlLength = 0;
        _state = ParserState.Osc;
        break;
      case 'P':
      case '^':
      case '_':
        _stringControlLength = 0;
        _state = ParserState.StringControl;
        break;
      case '(':
      case ')':
      case '*':
      case '+':
        _state = ParserState.CharacterSet;
        break;
      case '7':
        SaveCursor();
        break;
      case '8':
        RestoreCursor();
        break;
      case 'D':
        LineFeed();
        break;
      case 'E':
        _column = 0;
        LineFeed();
        break;
      case 'M':
        ReverseIndex();
        break;
      case 'c':
        Reset();
        break;
    }
  }

  private void ResolveCsi(string raw, char command) {
    bool privateMode = raw.StartsWith("?", StringComparison.Ordinal);
    string parameters = raw.TrimStart('?', '>', '!', '=');
    int[] values = parameters.Length == 0
        ? Array.Empty<int>()
        : parameters.Split(';').Select(ParseParameter).ToArray();
    _wrapPending = false;

    int Amount(int index = 0) {
      int value = index < values.Length ? values[index] : 1;
      return value == 0 ? 1 : value;
    }
    int Value(int index, int fallback = 0) => index < values.Length ? values[index] : fallback;

    switch (command) {
      case 'A':
        _row = Math.Max(0, _row - Amount());
        break;
      case 'B':
        _row = Math.Min(_height - 1, _row + Amount());
        break;
      case 'C':
      case 'a':
        _column = Math.Min(_width - 1, _column + Amount());
        break;
      case 'D':
        _column = Math.Max(0, _column - Amount());
        break;
      case 'E':
        _row = Math.Min(_height - 1, _row + Amount());
        _column = 0;
        break;
      case 'F':
        _row = Math.Max(0, _row - Amount());
        _column = 0;
        break;
      case 'G':
      case '`':
        _column = Math.Clamp(Amount() - 1, 0, _width - 1);
        break;
      case 'd':
        _row = Math.Clamp(Amount() - 1, 0, _height - 1);
        break;
      case 'H':
      case 'f':
        _row = Math.Clamp(Amount(0) - 1, 0, _height - 1);
        _column = Math.Clamp(Amount(1) - 1, 0, _width - 1);
        break;
      case 'J':
        EraseDisplay(Value(0));
        break;
      case 'K':
        EraseLine(Value(0));
        break;
      case 'L':
        InsertLines(Amount());
        break;
      case 'M':
        DeleteLines(Amount());
        break;
      case '@':
        InsertCharacters(Amount());
        break;
      case 'P':
        DeleteCharacters(Amount());
        break;
      case 'X':
        EraseCharacters(Amount());
        break;
      case 'S':
        ScrollUp(_scrollTop, _scrollBottom, Amount());
        break;
      case 'T':
        ScrollDown(_scrollTop, _scrollBottom, Amount());
        break;
      case 'r':
        SetScrollRegion(values);
        break;
      case 's':
        SaveCursor();
        break;
      case 'u':
        RestoreCursor();
        break;
      case 'h':
      case 'l':
        SetMode(values, privateMode, command == 'h');
        break;
      case 'n':
        if (Value(0) == 5) {
          ResponseGenerated?.Invoke("\u001b[0n");
        } else if (Value(0) == 6) {
          ResponseGenerated?.Invoke($"\u001b[{_row + 1};{_column + 1}R");
        }
        break;
      case 'c':
        ResponseGenerated?.Invoke("\u001b[?1;2c");
        break;
      case 'm':
        ApplySgr(values);
        break;
      default:
        break;
    }
  }

  private static int ParseParameter(string text) => int.TryParse(text, out int value) ? value : 0;

  private void SetMode(int[] values, bool privateMode, bool enabled) {
    if (!privateMode) {
      return;
    }
    foreach (int value in values) {
      switch (value) {
        case 1:
          ApplicationCursorKeys = enabled;
          break;
        case 7:
          _autoWrap = enabled;
          break;
        case 25:
          CursorVisible = enabled;
          break;
        case 47:
        case 1047:
        case 1049:
          if (enabled) {
            EnterAlternateScreen();
          } else {
            LeaveAlternateScreen();
          }
          break;
      }
    }
  }

  private void EnterAlternateScreen() {
    if (_primaryScreen != null) {
      return;
    }
    _primaryScreen = _screen;
    _primaryScreenAttributes = _screenAttributes;
    _primaryRow = _row;
    _primaryColumn = _column;
    _primaryAttributes = _attributes;
    _screen = NewScreen();
    _screenAttributes = NewAttributeScreen();
    _row = _column = 0;
    _scrollTop = 0;
    _scrollBottom = _height - 1;
  }

  private void LeaveAlternateScreen() {
    if (_primaryScreen == null) {
      return;
    }
    _screen = _primaryScreen;
    _primaryScreen = null;
    _screenAttributes = _primaryScreenAttributes!;
    _primaryScreenAttributes = null;
    _row = Math.Clamp(_primaryRow, 0, _height - 1);
    _column = Math.Clamp(_primaryColumn, 0, _width - 1);
    _attributes = _primaryAttributes;
    _scrollTop = 0;
    _scrollBottom = _height - 1;
  }

  private void SetScrollRegion(int[] values) {
    int top = values.Length > 0 && values[0] > 0 ? values[0] - 1 : 0;
    int bottom = values.Length > 1 && values[1] > 0 ? values[1] - 1 : _height - 1;
    if (top >= bottom || top < 0 || bottom >= _height) {
      return;
    }
    _scrollTop = top;
    _scrollBottom = bottom;
    _row = _column = 0;
  }

  private void SaveCursor() {
    _savedRow = _row;
    _savedColumn = _column;
    _savedAttributes = _attributes;
  }

  private void RestoreCursor() {
    _row = Math.Clamp(_savedRow, 0, _height - 1);
    _column = Math.Clamp(_savedColumn, 0, _width - 1);
    _attributes = _savedAttributes;
  }

  private void LineFeed() {
    _wrapPending = false;
    if (_row == _scrollBottom) {
      ScrollUp(_scrollTop, _scrollBottom, 1);
    } else {
      _row = Math.Min(_height - 1, _row + 1);
    }
  }

  private void ReverseIndex() {
    _wrapPending = false;
    if (_row == _scrollTop) {
      ScrollDown(_scrollTop, _scrollBottom, 1);
    } else {
      _row = Math.Max(0, _row - 1);
    }
  }

  private void ScrollUp(int top, int bottom, int count) {
    count = Math.Clamp(count, 1, bottom - top + 1);
    for (int row = top; row <= bottom - count; row++) {
      CopyRow(row + count, row);
    }
    for (int row = bottom - count + 1; row <= bottom; row++) {
      ClearRow(row);
    }
  }

  private void ScrollDown(int top, int bottom, int count) {
    count = Math.Clamp(count, 1, bottom - top + 1);
    for (int row = bottom; row >= top + count; row--) {
      CopyRow(row - count, row);
    }
    for (int row = top; row < top + count; row++) {
      ClearRow(row);
    }
  }

  private void CopyRow(int source, int target) {
    for (int column = 0; column < _width; column++) {
      _screen[target, column] = _screen[source, column];
      _screenAttributes[target, column] = _screenAttributes[source, column];
    }
  }

  private void ClearRow(int row) {
    for (int column = 0; column < _width; column++) {
      SetBlank(row, column);
    }
  }

  private void SetBlank(int row, int column) {
    _screen[row, column] = ' ';
    _screenAttributes[row, column] = _attributes;
  }

  private void FillScreenWithBlanks() {
    for (int row = 0; row < _height; row++) {
      ClearRow(row);
    }
  }

  private void EraseDisplay(int mode) {
    switch (mode) {
      case 1:
        for (int row = 0; row < _row; row++) {
          ClearRow(row);
        }
        for (int column = 0; column <= _column; column++) {
          SetBlank(_row, column);
        }
        break;
      case 2:
      case 3:
        FillScreenWithBlanks();
        break;
      default:
        for (int column = _column; column < _width; column++) {
          SetBlank(_row, column);
        }
        for (int row = _row + 1; row < _height; row++) {
          ClearRow(row);
        }
        break;
    }
  }

  private void EraseLine(int mode) {
    int start = mode == 0 ? _column : 0;
    int end = mode == 1 ? _column : _width - 1;
    if (mode == 2) {
      start = 0;
      end = _width - 1;
    }
    for (int column = start; column <= end; column++) {
      SetBlank(_row, column);
    }
  }

  private void InsertLines(int count) {
    if (_row < _scrollTop || _row > _scrollBottom) {
      return;
    }
    ScrollDown(_row, _scrollBottom, count);
  }

  private void DeleteLines(int count) {
    if (_row < _scrollTop || _row > _scrollBottom) {
      return;
    }
    ScrollUp(_row, _scrollBottom, count);
  }

  private void InsertCharacters(int count) {
    count = Math.Clamp(count, 1, _width - _column);
    for (int column = _width - 1; column >= _column + count; column--) {
      _screen[_row, column] = _screen[_row, column - count];
      _screenAttributes[_row, column] = _screenAttributes[_row, column - count];
    }
    for (int column = _column; column < _column + count; column++) {
      SetBlank(_row, column);
    }
  }

  private void DeleteCharacters(int count) {
    count = Math.Clamp(count, 1, _width - _column);
    for (int column = _column; column < _width - count; column++) {
      _screen[_row, column] = _screen[_row, column + count];
      _screenAttributes[_row, column] = _screenAttributes[_row, column + count];
    }
    for (int column = _width - count; column < _width; column++) {
      SetBlank(_row, column);
    }
  }

  private void EraseCharacters(int count) {
    int end = Math.Min(_width, _column + Math.Max(1, count));
    for (int column = _column; column < end; column++) {
      SetBlank(_row, column);
    }
  }

  private void ApplySgr(int[] values) {
    if (values.Length == 0) {
      _attributes = default;
      return;
    }

    for (int index = 0; index < values.Length; index++) {
      int value = values[index];
      switch (value) {
        case 0:
          _attributes = default;
          break;
        case 1:
          _attributes = _attributes with { Bold = true };
          break;
        case 3:
          _attributes = _attributes with { Italic = true };
          break;
        case 4:
          _attributes = _attributes with { Underline = true };
          break;
        case 7:
          _attributes = _attributes with { Inverse = true };
          break;
        case 22:
          _attributes = _attributes with { Bold = false };
          break;
        case 23:
          _attributes = _attributes with { Italic = false };
          break;
        case 24:
          _attributes = _attributes with { Underline = false };
          break;
        case 27:
          _attributes = _attributes with { Inverse = false };
          break;
        case >= 30 and <= 37:
          _attributes = _attributes with { Foreground = BasicColor(value - 30, false) };
          break;
        case 38:
          if (TryReadExtendedColor(values, ref index, out TerminalRgb foreground)) {
            _attributes = _attributes with { Foreground = foreground };
          }
          break;
        case 39:
          _attributes = _attributes with { Foreground = null };
          break;
        case >= 40 and <= 47:
          _attributes = _attributes with { Background = BasicColor(value - 40, false) };
          break;
        case 48:
          if (TryReadExtendedColor(values, ref index, out TerminalRgb background)) {
            _attributes = _attributes with { Background = background };
          }
          break;
        case 49:
          _attributes = _attributes with { Background = null };
          break;
        case >= 90 and <= 97:
          _attributes = _attributes with { Foreground = BasicColor(value - 90, true) };
          break;
        case >= 100 and <= 107:
          _attributes = _attributes with { Background = BasicColor(value - 100, true) };
          break;
      }
    }
  }

  private static bool TryReadExtendedColor(
      int[] values, ref int index, out TerminalRgb color) {
    color = default;
    if (index + 1 >= values.Length) {
      return false;
    }

    int mode = values[++index];
    if (mode == 5 && index + 1 < values.Length) {
      int paletteIndex = values[++index];
      if (paletteIndex is >= 0 and <= 255) {
        color = PaletteColor(paletteIndex);
        return true;
      }
      return false;
    }
    if (mode == 2 && index + 3 < values.Length) {
      int red = values[++index];
      int green = values[++index];
      int blue = values[++index];
      if (red is >= 0 and <= 255 && green is >= 0 and <= 255 &&
          blue is >= 0 and <= 255) {
        color = new TerminalRgb((byte)red, (byte)green, (byte)blue);
        return true;
      }
    }
    return false;
  }

  private static TerminalRgb BasicColor(int index, bool bright) {
    return (bright ? BrightColors : BasicColors)[Math.Clamp(index, 0, 7)];
  }

  private static TerminalRgb PaletteColor(int index) {
    if (index < FirstSixteenPaletteColors.Length) {
      return FirstSixteenPaletteColors[index];
    }
    if (index < 232) {
      int cube = index - 16;
      return new TerminalRgb(
          (byte)CubeLevels[cube / 36],
          (byte)CubeLevels[(cube / 6) % 6],
          (byte)CubeLevels[cube % 6]);
    }
    byte gray = (byte)(8 + ((index - 232) * 10));
    return new TerminalRgb(gray, gray, gray);
  }
}
