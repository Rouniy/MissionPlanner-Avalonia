using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Views;

public class WarningManagerWindow : Window {
  public WarningManagerWindow() {
    Title = "Warning Manager";
    Width = 1250;
    Height = 620;
    MinWidth = 1000;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var view = new WarningManagerView();
    var vm = new WarningManagerViewModel();
    view.DataContext = vm;
    Content = view;
    DataContext = vm;
  }

  public static void OpenWindow() {
    var window = new WarningManagerWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
