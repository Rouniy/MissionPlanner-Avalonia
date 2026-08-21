using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

public partial class ConfigTerminalView : UserControl {
  public ConfigTerminalView() {
    AvaloniaXamlLoader.Load(this);
    var input = this.FindControl<TextBox>("TerminalInput");
    input?.AddHandler(InputElement.KeyDownEvent, TerminalInput_OnKeyDown,
        RoutingStrategies.Tunnel, handledEventsToo: true);
    input?.AddHandler(InputElement.TextInputEvent, TerminalInput_OnTextInput,
        RoutingStrategies.Tunnel, handledEventsToo: true);
  }

  private void TerminalInput_OnKeyDown(object? sender, KeyEventArgs e) {
    if (DataContext is not ConfigTerminalViewModel viewModel) {
      return;
    }

    if (!viewModel.IsSshSession) {
      if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
          viewModel.SendCommand.CanExecute(null)) {
        viewModel.SendCommand.Execute(null);
        e.Handled = true;
      }
      return;
    }

    bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
    bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    if ((control && shift && e.Key == Key.V) || (shift && e.Key == Key.Insert)) {
      e.Handled = true;
      _ = PasteSshClipboard(viewModel);
      return;
    }
    if (control && shift && e.Key == Key.C) {
      e.Handled = true;
      _ = CopySshSelection();
      return;
    }

    string? sequence = viewModel.EncodeSshKey(e.Key, e.KeyModifiers);
    if (sequence != null) {
      viewModel.SendSshText(sequence);
      e.Handled = true;
    }
  }

  private void TerminalInput_OnTextInput(object? sender, TextInputEventArgs e) {
    if (DataContext is not ConfigTerminalViewModel { IsSshSession: true } viewModel ||
        string.IsNullOrEmpty(e.Text)) {
      return;
    }
    viewModel.SendSshText(e.Text);
    e.Handled = true;
  }

  private async System.Threading.Tasks.Task PasteSshClipboard(
      ConfigTerminalViewModel viewModel) {
    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
    string? text = clipboard == null ? null : await clipboard.TryGetTextAsync();
    if (!string.IsNullOrEmpty(text) && viewModel.IsSshSession) {
      viewModel.SendSshText(text);
    }
  }

  private async System.Threading.Tasks.Task CopySshSelection() {
    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
    var output = this.FindControl<AnsiTerminalView>("TerminalOutput");
    if (clipboard != null && !string.IsNullOrEmpty(output?.SelectedText)) {
      await clipboard.SetTextAsync(output.SelectedText);
    }
  }
}
