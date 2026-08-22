using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

public partial class ConfigElevationSourcesView : UserControl {
  public ConfigElevationSourcesView() {
    InitializeComponent();
    this.FindControl<Button>("BrowseButton")!.Click += BrowseDirectory;
  }

  private async void BrowseDirectory(object? sender, RoutedEventArgs e) {
    if (TopLevel.GetTopLevel(this) is not { } top) {
      return;
    }
    var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
      Title = "Select GeoTIFF / DTED elevation directory",
      AllowMultiple = false,
    });
    string? path = folders.FirstOrDefault()?.TryGetLocalPath();
    if (path != null && DataContext is ConfigElevationSourcesViewModel viewModel) {
      await viewModel.SelectAndScanAsync(path);
    }
  }
}
