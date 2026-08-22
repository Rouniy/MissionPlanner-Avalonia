using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public sealed class MapCacheWindow : Window {
  public MapCacheWindow() {
    Title = "Map Tile Cache";
    Width = 800;
    Height = 540;
    MinWidth = 650;
    MinHeight = 420;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var viewModel = new MapCacheViewModel();
    Content = new MapCacheView { DataContext = viewModel };
    DataContext = viewModel;
    Closed += (_, _) => viewModel.Dispose();
  }

  public static void OpenWindow() {
    var window = new MapCacheWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
