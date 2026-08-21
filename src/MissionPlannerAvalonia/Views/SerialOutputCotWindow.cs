using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public sealed class SerialOutputCotWindow : Window {
  private readonly SerialOutputCotViewModel _vm = new();

  public SerialOutputCotWindow() {
    Title = "Cursor-on-Target / TAK Output";
    Width = 720;
    Height = 820;
    MinWidth = 560;
    MinHeight = 650;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var view = new SerialOutputCotView { DataContext = _vm };
    Content = view;
    DataContext = _vm;
    Closing += (_, _) => view.CommitIdentityEdits();
    Closed += (_, _) => _vm.Dispose();
  }

  public static void OpenWindow() {
    var window = new SerialOutputCotWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
