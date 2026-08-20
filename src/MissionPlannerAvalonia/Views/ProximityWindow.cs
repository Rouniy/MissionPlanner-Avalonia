using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.Controls;

namespace MissionPlannerAvalonia.Views;

public class ProximityWindow : Window {
  public ProximityWindow() {
    Title = "Proximity";
    Width = 620;
    Height = 620;
    MinWidth = 420;
    MinHeight = 420;
    Background = new SolidColorBrush(Color.Parse("#151817"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Content = new ProximityRadarControl();
  }

  public static void OpenWindow() {
    var window = new ProximityWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
