using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class FaceMapView : UserControl {
  private static readonly FilePickerFileType _faceMapType = new("Mission Planner Face Map") {
    Patterns = ["*.facemap"],
  };

  private static readonly FilePickerFileType _jpegType = new("JPEG photo") {
    Patterns = ["*.jpg", "*.jpeg"],
  };

  public FaceMapView() {
    InitializeComponent();
  }

  private FaceMapViewModel? Vm => DataContext as FaceMapViewModel;

  private void OnFitPreview(object? sender, RoutedEventArgs e) => PreviewMap.ZoomToPreview();

  private async void OnLoadSamplePhoto(object? sender, RoutedEventArgs e) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || Vm == null) {
      return;
    }
    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load sample camera photo",
      AllowMultiple = false,
      FileTypeFilter = [_jpegType],
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
        "Camera Name", "Please enter a camera name",
        Vm.SelectedCamera.Length == 0 ? "Default" : Vm.SelectedCamera);
    if (name != null) {
      Vm.SaveCameraProfile(name);
    }
  }

  private async void OnLoadFaceMap(object? sender, RoutedEventArgs e) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || Vm == null) {
      return;
    }
    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load Face Map configuration",
      AllowMultiple = false,
      FileTypeFilter = [_faceMapType],
    });
    if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) {
      Vm.LoadFaceMapFile(path);
    }
  }

  private async void OnSaveFaceMap(object? sender, RoutedEventArgs e) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || Vm == null) {
      return;
    }
    IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(
        new FilePickerSaveOptions {
          Title = "Save Face Map configuration",
          DefaultExtension = "facemap",
          SuggestedFileName = "survey.facemap",
          FileTypeChoices = [_faceMapType],
        });
    if (file?.TryGetLocalPath() is { } path) {
      Vm.SaveFaceMapFile(path);
    }
  }
}
