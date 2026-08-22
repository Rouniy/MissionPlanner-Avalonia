using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public sealed class Terrain3DWindow : Window {
  public Terrain3DWindow() {
    Title = "3D Terrain View";
    Width = 1100;
    Height = 760;
    MinWidth = 720;
    MinHeight = 520;
    Background = new SolidColorBrush(Color.Parse("#151817"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var viewModel = new Terrain3DViewModel();
    DataContext = viewModel;
    Content = new Terrain3DView { DataContext = viewModel };
    Closed += (_, _) => viewModel.Dispose();
  }

  public static void OpenWindow() {
    var window = new Terrain3DWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
