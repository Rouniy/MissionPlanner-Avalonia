using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Views;

public class ConfigFFTWindow : Window {
  public ConfigFFTWindow() {
    Title = "FFT Log Analysis";
    Width = 1000;
    Height = 700;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var view = new ConfigFFTView();
    var vm = new ConfigFFTViewModel();
    view.DataContext = vm;
    Content = view;
    DataContext = vm;
  }

  public static void OpenWindow() {
    var window = new ConfigFFTWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
