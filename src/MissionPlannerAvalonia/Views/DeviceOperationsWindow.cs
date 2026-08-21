using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public sealed class DeviceOperationsWindow : Window {
  public DeviceOperationsWindow() {
    Title = "MAVLink Device Operations";
    Width = 760;
    Height = 520;
    MinWidth = 700;
    MinHeight = 420;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var viewModel = new DeviceOperationsViewModel();
    Content = new DeviceOperationsView { DataContext = viewModel };
    DataContext = viewModel;
  }

  public static void OpenWindow() {
    var window = new DeviceOperationsWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
