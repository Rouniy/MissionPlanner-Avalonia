using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public sealed class MicrodroneDownlinkWindow : Window {
  public MicrodroneDownlinkWindow() {
    Title = "MicroDrone Downlink";
    Width = 580;
    Height = 440;
    MinWidth = 520;
    MinHeight = 390;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var viewModel = new MicrodroneDownlinkViewModel();
    Content = new MicrodroneDownlinkView { DataContext = viewModel };
    DataContext = viewModel;
    Closed += (_, _) => viewModel.Dispose();
  }

  public static void OpenWindow() {
    var window = new MicrodroneDownlinkWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
