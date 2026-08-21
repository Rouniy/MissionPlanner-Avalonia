using Avalonia.Input;

namespace MissionPlannerAvalonia.Services;

internal static class TerminalKeyEncoder {
  public static string? Encode(Key key, KeyModifiers modifiers, bool applicationCursorKeys) {
    bool control = modifiers.HasFlag(KeyModifiers.Control);
    bool shift = modifiers.HasFlag(KeyModifiers.Shift);
    bool alt = modifiers.HasFlag(KeyModifiers.Alt);

    // Preserve the conventional Linux terminal clipboard shortcuts. Pasted text is
    // delivered through TextInput and then forwarded to the remote PTY.
    if ((control && shift && key is Key.C or Key.V) || (shift && key == Key.Insert)) {
      return null;
    }
    if (control && key is >= Key.A and <= Key.Z) {
      return ((char)((int)key - (int)Key.A + 1)).ToString();
    }
    if (control && key == Key.Space) {
      return "\0";
    }

    string? result = key switch {
      Key.Enter => "\r",
      Key.Back => "\u007f",
      Key.Tab when shift => "\u001b[Z",
      Key.Tab => "\t",
      Key.Escape => "\u001b",
      Key.Up => applicationCursorKeys ? "\u001bOA" : "\u001b[A",
      Key.Down => applicationCursorKeys ? "\u001bOB" : "\u001b[B",
      Key.Right => applicationCursorKeys ? "\u001bOC" : "\u001b[C",
      Key.Left => applicationCursorKeys ? "\u001bOD" : "\u001b[D",
      Key.Home => applicationCursorKeys ? "\u001bOH" : "\u001b[H",
      Key.End => applicationCursorKeys ? "\u001bOF" : "\u001b[F",
      Key.Insert => "\u001b[2~",
      Key.Delete => "\u001b[3~",
      Key.PageUp => "\u001b[5~",
      Key.PageDown => "\u001b[6~",
      Key.F1 => "\u001bOP",
      Key.F2 => "\u001bOQ",
      Key.F3 => "\u001bOR",
      Key.F4 => "\u001bOS",
      Key.F5 => "\u001b[15~",
      Key.F6 => "\u001b[17~",
      Key.F7 => "\u001b[18~",
      Key.F8 => "\u001b[19~",
      Key.F9 => "\u001b[20~",
      Key.F10 => "\u001b[21~",
      Key.F11 => "\u001b[23~",
      Key.F12 => "\u001b[24~",
      _ => null,
    };

    if (result != null) {
      return result;
    }
    if (alt && key is >= Key.A and <= Key.Z) {
      char letter = (char)('a' + ((int)key - (int)Key.A));
      return "\u001b" + letter;
    }
    return null;
  }
}
