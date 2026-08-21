using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public sealed class MovingBaseWindow : Window {
  private readonly MovingBaseViewModel _viewModel = new();

  public MovingBaseWindow() {
    Title = "Moving Base";
    Width = 580;
    Height = 500;
    MinWidth = 540;
    MinHeight = 460;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Content = new MovingBaseView { DataContext = _viewModel };
    DataContext = _viewModel;
    Closed += async (_, _) => {
      await _viewModel.StopAsync();
      _viewModel.Dispose();
    };
  }

  public static void OpenWindow() {
    var window = new MovingBaseWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
