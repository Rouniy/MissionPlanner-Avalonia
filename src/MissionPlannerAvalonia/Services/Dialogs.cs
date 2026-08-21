using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using UpstreamDialogResult = System.CustomMessageBox.DialogResult;
using UpstreamMessageBoxButtons = System.CustomMessageBox.MessageBoxButtons;
using UpstreamMessageBoxIcon = System.CustomMessageBox.MessageBoxIcon;

namespace MissionPlannerAvalonia.Services;

public static class Dialogs {
  private const string _bg = "#262728";

  public static Window? Owner =>
      (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

  public static Task<string?> InputBox(string title, string prompt, string value = "") =>
      ShowInput(title, prompt, value);

  public static Task<string?> PasswordInputBox(string title, string prompt) =>
      ShowInput(title, prompt, "", masked: true);

  public static Task<bool> Confirm(string title, string text) =>
      ShowButtons(title, text, ("Yes", true), ("No", false));

  /// <summary>
  /// Confirmation for a security-sensitive state change. Reject is both the default
  /// and cancel action, so Enter/Escape can never silently approve the operation.
  /// </summary>
  public static Task<bool> ConfirmDangerous(string title, string text, string acceptText) {
    var panel = Shell(title);
    panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
    var reject = new Button {
      Content = "Cancel",
      MinWidth = 80,
      IsDefault = true,
      IsCancel = true,
    };
    var accept = new Button { Content = acceptText, MinWidth = 110 };
    panel.Children.Add(Buttons(reject, accept));
    var window = Frame(title, panel);
    reject.Click += (_, _) => window.Close(false);
    accept.Click += (_, _) => window.Close(true);
    return ShowOwned<bool>(window);
  }

  public static Task Alert(string title, string text) =>
      ShowButtons(title, text, ("OK", true));

  // Several portable upstream libraries report failures through this compatibility
  // callback. The original Mission Planner wires it to a WinForms message box; leaving
  // it unset makes an otherwise recoverable error throw "ShowEvent Not Set".
  internal static UpstreamDialogResult ShowUpstreamMessage(string text, string caption,
      UpstreamMessageBoxButtons buttons, UpstreamMessageBoxIcon icon,
      string yesText, string noText) {
    var fallback = DefaultUpstreamResult(buttons);
    if (Owner == null) {
      Console.Error.WriteLine($"{(string.IsNullOrWhiteSpace(caption) ? "Mission Planner" : caption)}: {text}");
      return fallback;
    }

    if (Dispatcher.UIThread.CheckAccess()) {
      // The upstream API is synchronous, but a modal Avalonia dialog cannot be waited from
      // the UI thread itself. Current compiled callers are informational OK messages; post
      // the dialog and return the conservative result for any future multi-button caller.
      _ = ShowUpstreamMessageAsync(text, caption, buttons, yesText, noText);
      return fallback;
    }

    try {
      return Dispatcher.UIThread
          .InvokeAsync(() => ShowUpstreamMessageAsync(text, caption, buttons, yesText, noText))
          .GetAwaiter().GetResult();
    } catch (Exception ex) {
      Console.Error.WriteLine($"Unable to show upstream message: {ex.Message}\n{text}");
      return fallback;
    }
  }

  // Network transports and WinUSB use a ref-string input callback. Connection UI normally
  // supplies these values before Open(), but retaining the callback keeps less common paths
  // functional without pulling their original WinForms InputBox into the port.
  internal static inputboxreturn ShowUpstreamInput(string title, string prompt, ref string text) {
    if (Owner == null || Dispatcher.UIThread.CheckAccess()) {
      return inputboxreturn.NotSet;
    }

    try {
      var currentText = text;
      var entered = Dispatcher.UIThread.InvokeAsync(() => InputBox(title, prompt, currentText))
          .GetAwaiter().GetResult();
      if (entered == null) {
        return inputboxreturn.Cancel;
      }
      text = entered;
      return inputboxreturn.OK;
    } catch (Exception ex) {
      Console.Error.WriteLine($"Unable to show upstream input dialog: {ex.Message}");
      return inputboxreturn.NotSet;
    }
  }

  internal static UpstreamDialogResult DefaultUpstreamResult(UpstreamMessageBoxButtons buttons) =>
      buttons == UpstreamMessageBoxButtons.OK ? UpstreamDialogResult.OK : UpstreamDialogResult.Cancel;

  public static Task<string?> Choice(string title, string text, params string[] labels) {
    var panel = Shell(title);
    panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
    var row = new StackPanel {
      Orientation = Orientation.Horizontal,
      HorizontalAlignment = HorizontalAlignment.Right,
      Spacing = 8,
      Margin = new Thickness(0, 6, 0, 0),
    };
    var w = Frame(title, panel);
    foreach (var label in labels) {
      var b = new Button { Content = label, MinWidth = 80 };
      b.Click += (_, _) => w.Close(label);
      row.Children.Add(b);
    }
    panel.Children.Add(row);
    return ShowOwned<string?>(w);
  }

  public static Task<(double Alt, string Frame)?> AltInputBox(
      string title = "Altitude", double defaultAlt = 50, string defaultFrame = "Relative") =>
      ShowAltInput(title, defaultAlt, defaultFrame);

  public static async Task<bool> MessageShowAgain(string title, string text, string tag) {
    var key = "SHOWAGAIN_" + tag;
    if (Settings.Instance.GetBoolean(key)) {
      return true;
    }
    var (ok, dontShow) = await ShowShowAgain(title, text);
    if (dontShow) {
      Settings.Instance[key] = true.ToString();
      Settings.Instance.Save();
    }
    return ok;
  }

  public static void OpenUrl(string url) {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
      Process.Start("xdg-open", url);
    } else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
      Process.Start("open", url);
    } else {
      Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
  }

  private static StackPanel Shell(string title) => new() {
    Margin = new Thickness(16),
    Spacing = 10,
    Children = { new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 14 } },
  };

  private static Window Frame(string title, Control body) => new() {
    Title = title,
    Width = 380,
    SizeToContent = SizeToContent.Height,
    CanResize = false,
    WindowStartupLocation = WindowStartupLocation.CenterOwner,
    Background = new SolidColorBrush(Color.Parse(_bg)),
    Content = body,
  };

  private static Task<string?> ShowInput(string title, string prompt, string value,
      bool masked = false) {
    var box = new TextBox { Text = value };
    if (masked) {
      box.PasswordChar = '●';
    }
    var panel = Shell(title);
    panel.Children.Add(new TextBlock { Text = prompt });
    panel.Children.Add(box);
    var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
    var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
    panel.Children.Add(Buttons(ok, cancel));
    var w = Frame(title, panel);
    ok.Click += (_, _) => w.Close(box.Text);
    cancel.Click += (_, _) => w.Close((string?)null);
    return ShowOwned<string?>(w);
  }

  private static async Task<(double, string)?> ShowAltInput(string title, double alt, string frame) {
    var box = new TextBox { Text = alt.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    var combo = new ComboBox {
      ItemsSource = new[] { "Relative", "Absolute", "Terrain" },
      SelectedItem = frame,
      HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    var panel = Shell(title);
    panel.Children.Add(new TextBlock { Text = "Altitude" });
    panel.Children.Add(box);
    panel.Children.Add(new TextBlock { Text = "Frame" });
    panel.Children.Add(combo);
    var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
    var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
    panel.Children.Add(Buttons(ok, cancel));
    var w = Frame(title, panel);
    ok.Click += (_, _) => w.Close(true);
    cancel.Click += (_, _) => w.Close(false);
    if (!await ShowOwned<bool>(w)) {
      return null;
    }
    if (!double.TryParse(box.Text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var a)) {
      return null;
    }
    return (a, combo.SelectedItem as string ?? "Relative");
  }

  private static Task<bool> ShowButtons(string title, string text, params (string label, bool result)[] btns) {
    return ShowButtons<bool>(title, text, btns);
  }

  private static Task<T> ShowButtons<T>(string title, string text, params (string label, T result)[] btns) {
    var panel = Shell(title);
    panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
    var row = new StackPanel {
      Orientation = Orientation.Horizontal,
      HorizontalAlignment = HorizontalAlignment.Right,
      Spacing = 8,
      Margin = new Thickness(0, 6, 0, 0),
    };
    var w = Frame(title, panel);
    foreach (var (label, result) in btns) {
      var b = new Button {
        Content = label,
        MinWidth = 80,
        IsDefault = System.Collections.Generic.EqualityComparer<T>.Default.Equals(result, btns[0].result),
      };
      b.Click += (_, _) => w.Close(result);
      row.Children.Add(b);
    }
    panel.Children.Add(row);
    return ShowOwned<T>(w);
  }

  private static Task<UpstreamDialogResult> ShowUpstreamMessageAsync(string text, string caption,
      UpstreamMessageBoxButtons buttons, string yesText, string noText) {
    var title = string.IsNullOrWhiteSpace(caption) ? "Mission Planner" : caption;
    return buttons switch {
      UpstreamMessageBoxButtons.OK =>
          ShowButtons(title, text, ("OK", UpstreamDialogResult.OK)),
      UpstreamMessageBoxButtons.OKCancel =>
          ShowButtons(title, text, ("OK", UpstreamDialogResult.OK), ("Cancel", UpstreamDialogResult.Cancel)),
      UpstreamMessageBoxButtons.AbortRetryIgnore =>
          ShowButtons(title, text, ("Abort", UpstreamDialogResult.Abort), ("Retry", UpstreamDialogResult.Retry),
              ("Ignore", UpstreamDialogResult.Ignore)),
      UpstreamMessageBoxButtons.YesNoCancel =>
          ShowButtons(title, text, (yesText, UpstreamDialogResult.Yes), (noText, UpstreamDialogResult.No),
              ("Cancel", UpstreamDialogResult.Cancel)),
      UpstreamMessageBoxButtons.YesNo =>
          ShowButtons(title, text, (yesText, UpstreamDialogResult.Yes), (noText, UpstreamDialogResult.No)),
      UpstreamMessageBoxButtons.RetryCancel =>
          ShowButtons(title, text, ("Retry", UpstreamDialogResult.Retry),
              ("Cancel", UpstreamDialogResult.Cancel)),
      _ => ShowButtons(title, text, ("OK", UpstreamDialogResult.OK)),
    };
  }

  private static async Task<(bool ok, bool dontShow)> ShowShowAgain(string title, string text) {
    var chk = new CheckBox { Content = "Don't show this again" };
    var panel = Shell(title);
    panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
    panel.Children.Add(chk);
    var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
    var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
    var w = Frame(title, panel);
    ok.Click += (_, _) => w.Close(true);
    cancel.Click += (_, _) => w.Close(false);
    panel.Children.Add(Buttons(ok, cancel));
    var ok2 = await ShowOwned<bool>(w);
    return (ok2, chk.IsChecked == true);
  }

  private static StackPanel Buttons(params Control[] controls) {
    var row = new StackPanel {
      Orientation = Orientation.Horizontal,
      HorizontalAlignment = HorizontalAlignment.Right,
      Spacing = 8,
      Margin = new Thickness(0, 6, 0, 0),
    };
    foreach (var c in controls) {
      row.Children.Add(c);
    }
    return row;
  }

  private static Task<T> ShowOwned<T>(Window w) {
    var owner = Owner;
    if (owner != null) {
      return w.ShowDialog<T>(owner);
    }
    // No main window to own a modal dialog (early startup or shutdown): show standalone.
    // The dialog result cannot be observed this way, so callers see a cancelled dialog.
    var tcs = new TaskCompletionSource<T>();
    w.Closed += (_, _) => tcs.TrySetResult(default!);
    w.Show();
    return tcs.Task;
  }
}
