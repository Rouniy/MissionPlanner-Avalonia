using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class LogIndexWindow : Window {
  private static LogIndexWindow? _current;
  private bool _allowClose;
  private bool _closePromptOpen;
  private bool _initialScanStarted;

  public LogIndexWindow() : this(new LogIndexViewModel(Settings.Instance.LogDir)) {
  }

  internal LogIndexWindow(LogIndexViewModel viewModel) {
    InitializeComponent();
    DataContext = viewModel;
    DefaultDirectoryButton.Click += OnDefaultDirectory;
    ChooseDirectoryButton.Click += OnChooseDirectory;
    RefreshButton.Click += OnRefresh;
    CancelButton.Click += (_, _) => viewModel.Cancel();
    DeleteButton.Click += OnDelete;
    LogGrid.SelectionChanged += OnSelectionChanged;
    LogGrid.DoubleTapped += OnDoubleTapped;
    Opened += OnOpened;
    Closing += OnClosing;
    Closed += (_, _) => {
      viewModel.Dispose();
      if (ReferenceEquals(_current, this)) {
        _current = null;
      }
    };
  }

  public static void OpenWindow(string? rootDirectory = null) {
    if (_current != null) {
      _current.Activate();
      return;
    }
    string root = string.IsNullOrWhiteSpace(rootDirectory) ? Settings.Instance.LogDir : rootDirectory;
    var window = new LogIndexWindow(new LogIndexViewModel(root));
    _current = window;
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private async void OnOpened(object? sender, EventArgs e) {
    if (_initialScanStarted || DataContext is not LogIndexViewModel viewModel) {
      return;
    }
    _initialScanStarted = true;
    await viewModel.RefreshAsync();
  }

  private async void OnDefaultDirectory(object? sender, RoutedEventArgs e) {
    if (DataContext is LogIndexViewModel viewModel) {
      await viewModel.ScanAsync(Settings.Instance.LogDir);
    }
  }

  private async void OnChooseDirectory(object? sender, RoutedEventArgs e) {
    if (DataContext is not LogIndexViewModel viewModel) {
      return;
    }
    var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
      Title = "Select directory containing flight logs",
      AllowMultiple = false,
    });
    string? path = folders.FirstOrDefault()?.TryGetLocalPath();
    if (path != null) {
      await viewModel.ScanAsync(path);
    }
  }

  private async void OnRefresh(object? sender, RoutedEventArgs e) {
    if (DataContext is LogIndexViewModel viewModel) {
      await viewModel.RefreshAsync();
    }
  }

  private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) {
    if (DataContext is LogIndexViewModel viewModel) {
      viewModel.UpdateSelection(LogGrid.SelectedItems?.OfType<LogIndexRow>() ?? []);
    }
  }

  private async void OnDelete(object? sender, RoutedEventArgs e) {
    if (DataContext is LogIndexViewModel viewModel) {
      await viewModel.DeleteSelectedAsync(
          LogGrid.SelectedItems?.OfType<LogIndexRow>().ToArray() ?? []);
    }
  }

  private async void OnDoubleTapped(object? sender, TappedEventArgs e) {
    if (LogGrid.SelectedItem is LogIndexRow row) {
      await LogBrowseWindow.OpenWith(row.FullPath);
    }
  }

  private async void OnClosing(object? sender, WindowClosingEventArgs e) {
    if (DataContext is not LogIndexViewModel viewModel) {
      return;
    }
    if (_allowClose || !viewModel.IsBusy) {
      return;
    }
    e.Cancel = true;
    if (_closePromptOpen) {
      return;
    }
    _closePromptOpen = true;
    try {
      if (!await Services.Dialogs.ConfirmDangerous(
              "Cancel log-index operation?",
              "A log scan or deletion is still running. Cancel it and close the index?",
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
}
