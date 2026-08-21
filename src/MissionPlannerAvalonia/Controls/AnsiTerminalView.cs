using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Controls;

/// <summary>
/// Selectable Avalonia renderer for the SSH terminal screen. Styled runs retain
/// xterm cell colours and font attributes while a reversed cell represents the
/// remote cursor without sacrificing native text selection/copy.
/// </summary>
public sealed class AnsiTerminalView : SelectableTextBlock {
  private static readonly TerminalRgb DefaultForeground = new(221, 221, 221);
  private static readonly TerminalRgb DefaultBackground = new(16, 16, 16);

  public static readonly StyledProperty<TerminalSnapshot?> ScreenProperty =
      AvaloniaProperty.Register<AnsiTerminalView, TerminalSnapshot?>(nameof(Screen));

  public static readonly StyledProperty<string?> PlainTextProperty =
      AvaloniaProperty.Register<AnsiTerminalView, string?>(nameof(PlainText));

  public static readonly StyledProperty<bool> ShowCursorProperty =
      AvaloniaProperty.Register<AnsiTerminalView, bool>(nameof(ShowCursor));

  static AnsiTerminalView() {
    ScreenProperty.Changed.AddClassHandler<AnsiTerminalView>((view, _) => view.Rebuild());
    PlainTextProperty.Changed.AddClassHandler<AnsiTerminalView>((view, _) => view.Rebuild());
    ShowCursorProperty.Changed.AddClassHandler<AnsiTerminalView>((view, _) => view.Rebuild());
  }

  public AnsiTerminalView() {
    Inlines = new InlineCollection();
    Rebuild();
  }

  public TerminalSnapshot? Screen {
    get => GetValue(ScreenProperty);
    set => SetValue(ScreenProperty, value);
  }

  public string? PlainText {
    get => GetValue(PlainTextProperty);
    set => SetValue(PlainTextProperty, value);
  }

  public bool ShowCursor {
    get => GetValue(ShowCursorProperty);
    set => SetValue(ShowCursorProperty, value);
  }

  private void Rebuild() {
    int selectionStart = SelectionStart;
    int selectionEnd = SelectionEnd;
    Inlines ??= new InlineCollection();
    Inlines.Clear();

    int renderedLength;
    if (Screen is { } screen) {
      renderedLength = BuildScreen(screen);
    } else {
      string text = PlainText ?? "";
      if (text.Length > 0) {
        Inlines.Add(CreateRun(text, default, cursor: false));
      }
      renderedLength = text.Length;
    }

    SelectionStart = Math.Clamp(selectionStart, 0, renderedLength);
    SelectionEnd = Math.Clamp(selectionEnd, 0, renderedLength);
  }

  private int BuildScreen(TerminalSnapshot screen) {
    bool drawCursor = ShowCursor && screen.CursorVisible;
    int lastRow = drawCursor ? screen.CursorRow : 0;
    for (int row = 0; row < screen.Height; row++) {
      for (int column = screen.Width - 1; column >= 0; column--) {
        if (IsVisibleCell(screen[row, column])) {
          lastRow = Math.Max(lastRow, row);
          break;
        }
      }
    }

    int renderedLength = 0;
    for (int row = 0; row <= lastRow; row++) {
      int lastColumn = -1;
      for (int column = screen.Width - 1; column >= 0; column--) {
        if (IsVisibleCell(screen[row, column])) {
          lastColumn = column;
          break;
        }
      }
      if (drawCursor && row == screen.CursorRow) {
        lastColumn = Math.Max(lastColumn, screen.CursorColumn);
      }

      if (lastColumn >= 0) {
        var text = new StringBuilder();
        TerminalAttributes attributes = screen[row, 0].Attributes;
        bool cursor = drawCursor && row == screen.CursorRow && screen.CursorColumn == 0;
        for (int column = 0; column <= lastColumn; column++) {
          TerminalCell cell = screen[row, column];
          bool cellCursor = drawCursor && row == screen.CursorRow &&
              column == screen.CursorColumn;
          if (text.Length > 0 &&
              (cell.Attributes != attributes || cellCursor != cursor)) {
            Inlines!.Add(CreateRun(text.ToString(), attributes, cursor));
            text.Clear();
          }
          attributes = cell.Attributes;
          cursor = cellCursor;
          text.Append(cell.Character == '\0' ? ' ' : cell.Character);
          renderedLength++;
        }
        if (text.Length > 0) {
          Inlines!.Add(CreateRun(text.ToString(), attributes, cursor));
        }
      }

      if (row < lastRow) {
        Inlines!.Add(new LineBreak());
        renderedLength++;
      }
    }
    return renderedLength;
  }

  private static bool IsVisibleCell(TerminalCell cell) =>
      cell.Character is not (' ' or '\0') || cell.Attributes.Background != null ||
      cell.Attributes.Inverse || cell.Attributes.Underline;

  private static Run CreateRun(
      string text, TerminalAttributes attributes, bool cursor) {
    TerminalRgb foreground = attributes.Foreground ?? DefaultForeground;
    TerminalRgb background = attributes.Background ?? DefaultBackground;
    if (attributes.Inverse) {
      (foreground, background) = (background, foreground);
    }
    if (foreground == background) {
      foreground = new TerminalRgb(127, 127, 127);
    }
    if (cursor) {
      (foreground, background) = (background, foreground);
    }

    return new Run(text) {
      Foreground = Brush(foreground),
      Background = attributes.Background != null || attributes.Inverse || cursor
          ? Brush(background)
          : null,
      FontWeight = attributes.Bold ? FontWeight.Bold : FontWeight.Normal,
      FontStyle = attributes.Italic ? FontStyle.Italic : FontStyle.Normal,
      TextDecorations = attributes.Underline
          ? Avalonia.Media.TextDecorations.Underline
          : null,
    };
  }

  private static SolidColorBrush Brush(TerminalRgb color) =>
      new(Color.FromRgb(color.Red, color.Green, color.Blue));
}
