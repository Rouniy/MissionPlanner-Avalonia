using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class OsdVideoOverlayWindow : Window {
  private static OsdVideoOverlayWindow? _current;
  private readonly AvaloniaOsdVideoFrameRenderer _renderer;
  private bool _allowClose;
  private bool _closePromptOpen;

  public OsdVideoOverlayWindow() : this(new OsdVideoOverlayViewModel()) {
  }

  internal OsdVideoOverlayWindow(OsdVideoOverlayViewModel viewModel) {
    InitializeComponent();
    Image preview = this.FindControl<Image>("PreviewImage")
        ?? throw new InvalidOperationException("OSD video preview control is missing.");
    _renderer = new AvaloniaOsdVideoFrameRenderer(preview);
    viewModel.SetRenderer(_renderer);
    DataContext = viewModel;
    Closing += OnClosing;
    Closed += (_, _) => {
      _renderer.Dispose();
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
    var window = new OsdVideoOverlayWindow();
    _current = window;
    if (Services.Dialogs.Owner is { } owner) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private void OnClose(object? sender, RoutedEventArgs e) => Close();

  private async void OnClosing(object? sender, WindowClosingEventArgs e) {
    if (_allowClose || DataContext is not OsdVideoOverlayViewModel { IsBusy: true } viewModel) {
      return;
    }
    e.Cancel = true;
    if (_closePromptOpen) {
      return;
    }
    _closePromptOpen = true;
    try {
      if (!await Services.Dialogs.ConfirmDangerous(
              "Cancel OSD video export?",
              "Video rendering is still in progress. Cancel it, finalize the playable partial AVI, and close?",
              "CANCEL AND CLOSE")) {
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
