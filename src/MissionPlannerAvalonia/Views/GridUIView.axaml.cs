using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class GridUIView : UserControl {
  private static readonly FilePickerFileType _gridType = new("Mission Planner survey grid") {
    Patterns = new[] { "*.grid" },
  };

  private static readonly FilePickerFileType _jpegType = new("JPEG photo") {
    Patterns = new[] { "*.jpg", "*.jpeg" },
  };

  public GridUIView() {
    InitializeComponent();
  }

  private GridUIViewModel? Vm => DataContext as GridUIViewModel;

  private void OnFitPreview(object? sender, RoutedEventArgs e) => PreviewMap.ZoomToPreview();

  private void OnBoundaryEditMode(object? sender, RoutedEventArgs e) {
    if (sender is not RadioButton { IsChecked: true, Tag: string value } ||
        !Enum.TryParse(value, out SurveyBoundaryEditMode mode)) {
      return;
    }
    PreviewMap.EditMode = mode;
    if (Vm != null) {
      Vm.Status = mode switch {
        SurveyBoundaryEditMode.Pan => "Drag a numbered vertex to edit the survey boundary.",
        SurveyBoundaryEditMode.DrawRectangle =>
            "Drag on the map to replace the boundary with a rectangle.",
        SurveyBoundaryEditMode.ShiftEdge =>
            "Drag to move the nearest boundary edge perpendicular to itself.",
        SurveyBoundaryEditMode.MoveBoundary => "Drag to move the complete survey boundary.",
        _ => Vm.Status,
      };
    }
  }

  private async void OnLoadSamplePhoto(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top == null || Vm == null) {
      return;
    }
    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load sample camera photo",
      AllowMultiple = false,
      FileTypeFilter = new[] { _jpegType },
    });
    if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) {
      Vm.LoadSamplePhoto(path);
    }
  }

  private async void OnSaveCameraProfile(object? sender, RoutedEventArgs e) {
    if (Vm == null) {
      return;
    }
    string? name = await Services.Dialogs.InputBox(
        "Camera Name", "Please enter a camera name", Vm.SelectedCamera.Length == 0
            ? "Default"
            : Vm.SelectedCamera);
    if (name != null) {
      Vm.SaveCameraProfile(name);
    }
  }

  private async void OnLoadGrid(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top == null || Vm == null) {
      return;
    }
    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load survey configuration",
      AllowMultiple = false,
      FileTypeFilter = new[] { _gridType },
    });
    if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) {
      Vm.LoadGridFile(path);
    }
  }

  private async void OnSaveGrid(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top == null || Vm == null) {
      return;
    }
    var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save survey configuration",
      DefaultExtension = "grid",
      SuggestedFileName = "survey.grid",
      FileTypeChoices = new[] { _gridType },
    });
    if (file?.TryGetLocalPath() is { } path) {
      Vm.SaveGridFile(path);
    }
  }
}
