using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

public partial class ConfigPlannerView : UserControl {
  public ConfigPlannerView() {
    AvaloniaXamlLoader.Load(this);
    this.FindControl<Button>("LogDirBrowse")!.Click += BrowseLogDirectory;
  }

  private async void BrowseLogDirectory(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top == null) {
      return;
    }

    var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
      Title = "Select telemetry log directory",
      AllowMultiple = false,
    });
    var path = folders.FirstOrDefault()?.TryGetLocalPath();
    if (path != null && DataContext is ConfigPlannerViewModel vm) {
      vm.LogDir = path;
    }
  }
}
