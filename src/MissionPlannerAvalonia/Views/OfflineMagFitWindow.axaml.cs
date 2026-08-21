using System;
using Avalonia.Controls;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class OfflineMagFitWindow : Window {
  private static OfflineMagFitWindow? _current;
  private bool _allowClose;
  private bool _closePromptOpen;
  private bool _initialAnalysisStarted;

  public OfflineMagFitWindow() : this(new OfflineMagFitViewModel()) {
  }

  internal OfflineMagFitWindow(OfflineMagFitViewModel viewModel) {
    InitializeComponent();
    DataContext = viewModel;
    Opened += OnOpened;
    Closing += OnClosing;
    Closed += (_, _) => {
      viewModel.Dispose();
      if (ReferenceEquals(_current, this)) {
        _current = null;
      }
    };
  }

  public static void OpenWindow(string? sourcePath = null) {
    if (_current != null) {
      if (!string.IsNullOrWhiteSpace(sourcePath)
          && _current.DataContext is OfflineMagFitViewModel currentViewModel) {
        if (currentViewModel.SetSource(sourcePath)) {
          _ = currentViewModel.AnalyzeAsync();
        }
      }
      _current.Activate();
      return;
    }
    var viewModel = new OfflineMagFitViewModel(sourcePath);
    var window = new OfflineMagFitWindow(viewModel);
    _current = window;
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private async void OnOpened(object? sender, EventArgs e) {
    if (_initialAnalysisStarted || DataContext is not OfflineMagFitViewModel viewModel
        || !viewModel.HasInitialSource) {
      return;
    }
    _initialAnalysisStarted = true;
    await viewModel.AnalyzeAsync();
  }

  private async void OnClosing(object? sender, WindowClosingEventArgs e) {
    if (_allowClose || DataContext is not OfflineMagFitViewModel { IsBusy: true } viewModel) {
      return;
    }
    e.Cancel = true;
    if (_closePromptOpen) {
      return;
    }
    _closePromptOpen = true;
    try {
      if (!await Services.Dialogs.ConfirmDangerous(
              "Cancel offline MagFit?",
              "Log analysis or parameter writing is still in progress. Cancel analysis or wait for the "
              + "current parameter write, then close? An in-progress write cannot be rolled back.",
              "Finish and close")) {
        return;
      }
      await viewModel.CancelAndWaitAsync();
      _allowClose = true;
      Close();
    } finally {
      _closePromptOpen = false;
    }
  }
}
