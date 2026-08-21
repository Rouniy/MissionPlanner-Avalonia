using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class SftpLogDownloadWindow : Window {
  private bool _allowClose;
  private bool _closePromptOpen;

  public SftpLogDownloadWindow() : this(new SftpLogDownloadViewModel()) {
  }

  internal SftpLogDownloadWindow(SftpLogDownloadViewModel viewModel) {
    AvaloniaXamlLoader.Load(this);
    DataContext = viewModel;
    Closing += OnClosing;
  }

  private async void OnClosing(object? sender, WindowClosingEventArgs e) {
    if (DataContext is not SftpLogDownloadViewModel viewModel) {
      return;
    }
    if (_allowClose || !viewModel.IsBusy) {
      viewModel.Dispose();
      return;
    }

    e.Cancel = true;
    if (_closePromptOpen) {
      return;
    }
    _closePromptOpen = true;
    try {
      if (!await Services.Dialogs.ConfirmDangerous(
              "Cancel SFTP operation?",
              "A remote log operation is still running. Cancel it, remove any partial local file, and close?",
              "Cancel and close")) {
        return;
      }
      await viewModel.CancelAndWaitAsync();
      _allowClose = true;
      Close();
    } finally {
      _closePromptOpen = false;
    }
  }

  public static void OpenWindow() {
    var window = new SftpLogDownloadWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
