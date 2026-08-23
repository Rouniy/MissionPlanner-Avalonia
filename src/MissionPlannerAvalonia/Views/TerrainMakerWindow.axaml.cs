using Avalonia.Controls;
using Avalonia.Interactivity;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class TerrainMakerWindow : Window {
  private static TerrainMakerWindow? _current;
  private bool _allowClose;
  private bool _closePromptOpen;

  public TerrainMakerWindow() : this(new TerrainMakerViewModel(
      new TerrainBounds(34.5, 32.5, 35.5, 33.5))) {
  }

  internal TerrainMakerWindow(TerrainMakerViewModel viewModel) {
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

  internal static void OpenWindow(TerrainBounds visibleBounds) {
    if (_current != null) {
      if (_current.DataContext is TerrainMakerViewModel { IsBusy: false } viewModel) {
        viewModel.South = decimal.Round((decimal)visibleBounds.South, 7);
        viewModel.West = decimal.Round((decimal)visibleBounds.West, 7);
        viewModel.North = decimal.Round((decimal)visibleBounds.North, 7);
        viewModel.East = decimal.Round((decimal)visibleBounds.East, 7);
      }
      _current.Activate();
      return;
    }
    var window = new TerrainMakerWindow(new TerrainMakerViewModel(visibleBounds));
    _current = window;
    if (Dialogs.Owner is { } owner) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private void OnClose(object? sender, RoutedEventArgs e) => Close();

  private async void OnClosing(object? sender, WindowClosingEventArgs e) {
    if (_allowClose || DataContext is not TerrainMakerViewModel { IsBusy: true } viewModel) {
      return;
    }
    e.Cancel = true;
    if (_closePromptOpen) {
      return;
    }
    _closePromptOpen = true;
    try {
      if (!await Dialogs.ConfirmDangerous(
              "Cancel Terrain DAT generation?",
              "A terrain tile is still being generated. Cancel it, remove its temporary file, "
                  + "retain already completed tiles, and close?",
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
