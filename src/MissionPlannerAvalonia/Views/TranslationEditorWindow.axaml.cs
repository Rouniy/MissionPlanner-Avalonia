using Avalonia.Controls;
using Avalonia.Interactivity;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class TranslationEditorWindow : Window {
  private static TranslationEditorWindow? _current;
  private bool _allowClose;
  private bool _closePromptOpen;

  public TranslationEditorWindow() : this(new TranslationEditorViewModel()) {
  }

  internal TranslationEditorWindow(TranslationEditorViewModel viewModel) {
    InitializeComponent();
    DataContext = viewModel;
    Closing += OnClosing;
    Closed += (_, _) => {
      viewModel.Dispose();
      if (ReferenceEquals(_current, this)) {
        _current = null;
      }
    };
  }

  public static void OpenWindow() {
    if (_current != null) {
      _current.Activate();
      return;
    }
    var window = new TranslationEditorWindow();
    _current = window;
    if (Services.Dialogs.Owner is { } owner) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private void OnClose(object? sender, RoutedEventArgs e) => Close();

  private async void OnClosing(object? sender, WindowClosingEventArgs e) {
    if (_allowClose || DataContext is not TranslationEditorViewModel viewModel
        || (!viewModel.IsBusy && !viewModel.HasUnsavedChanges)) {
      return;
    }
    e.Cancel = true;
    if (_closePromptOpen) {
      return;
    }
    _closePromptOpen = true;
    try {
      string message = viewModel.IsBusy
          ? "A resource scan or export is still running. Cancel it and close?"
          : "There are translation edits that have not been exported. Discard them and close?";
      if (!await Services.Dialogs.Confirm("Close Translation Editor?", message)) {
        return;
      }
      if (viewModel.IsBusy) {
        await viewModel.CancelAndWaitAsync();
      }
      _allowClose = true;
      Close();
    } finally {
      _closePromptOpen = false;
    }
  }
}
