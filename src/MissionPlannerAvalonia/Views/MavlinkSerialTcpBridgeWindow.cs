using Avalonia.Controls;
using Avalonia.Media;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public sealed class MavlinkSerialTcpBridgeWindow : Window {
  private static MavlinkSerialTcpBridgeWindow? _current;
  private readonly MavlinkSerialTcpBridgeViewModel _viewModel = new();

  public MavlinkSerialTcpBridgeWindow() {
    Title = "MAVLink Serial TCP Bridge";
    Width = 680;
    Height = 570;
    MinWidth = 620;
    MinHeight = 520;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Content = new MavlinkSerialTcpBridgeView { DataContext = _viewModel };
    DataContext = _viewModel;
    Closed += async (_, _) => {
      await _viewModel.StopAsync();
      _viewModel.Dispose();
      if (ReferenceEquals(_current, this)) {
        _current = null;
      }
    };
  }

  public static void OpenWindow() {
    if (_current != null) {
      _current.Activate();
      return;
    }
    var window = new MavlinkSerialTcpBridgeWindow();
    _current = window;
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
