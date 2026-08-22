using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class SwarmSequenceWindow : Window {
  private static SwarmSequenceWindow? _instance;
  private readonly SwarmSequenceViewModel _viewModel = new();

  public SwarmSequenceWindow() {
    InitializeComponent();
    DataContext = _viewModel;
    Closed += (_, _) => {
      SequenceLayoutGrid.Dispose();
      _viewModel.Dispose();
      if (ReferenceEquals(_instance, this)) {
        _instance = null;
      }
    };
  }

  public static void OpenWindow() {
    if (_instance is { } existing) {
      existing.Activate();
      return;
    }
    var window = new SwarmSequenceWindow();
    _instance = window;
    Window? owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private async void OnLoad(object? sender, RoutedEventArgs e) {
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load Mission Planner Sequence",
      AllowMultiple = false,
      FileTypeFilter = [new FilePickerFileType("Mission Planner Sequence") {
        Patterns = ["*.txt", "*.json"],
      }],
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path != null) {
      await _viewModel.LoadAsync(path);
    }
  }

  private async void OnSave(object? sender, RoutedEventArgs e) {
    var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save Mission Planner Sequence",
      SuggestedFileName = "swarm-sequence.txt",
      DefaultExtension = "txt",
      FileTypeChoices = [new FilePickerFileType("Mission Planner Sequence") {
        Patterns = ["*.txt", "*.json"],
      }],
    });
    string? path = file?.TryGetLocalPath();
    if (path != null) {
      await _viewModel.SaveAsync(path);
    }
  }

  private async void OnBackgroundImage(object? sender, RoutedEventArgs e) {
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Sequence Background Image",
      AllowMultiple = false,
      FileTypeFilter = [new FilePickerFileType("Images") {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif"],
      }],
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path == null) {
      return;
    }
    try {
      SequenceLayoutGrid.LoadBackground(path);
    } catch (System.Exception ex) {
      await Services.Dialogs.Alert("Sequence Background", ex.Message);
    }
  }

  private void OnImageLeft(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.MoveBackground(-1, 0);
  private void OnImageRight(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.MoveBackground(1, 0);
  private void OnImageUp(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.MoveBackground(0, -1);
  private void OnImageDown(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.MoveBackground(0, 1);
  private void OnImageWider(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.ResizeBackground(1, 0);
  private void OnImageNarrower(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.ResizeBackground(-1, 0);
  private void OnImageTaller(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.ResizeBackground(0, 1);
  private void OnImageShorter(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.ResizeBackground(0, -1);
  private void OnImageFineStep(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.SetBackgroundStep(0.1);
  private void OnImageNormalStep(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.SetBackgroundStep(1);
  private void OnImageClear(object? sender, RoutedEventArgs e) =>
      SequenceLayoutGrid.ClearBackground();
  private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
