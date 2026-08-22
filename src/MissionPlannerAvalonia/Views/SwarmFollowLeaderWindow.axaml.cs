using Avalonia.Controls;
using Avalonia.Interactivity;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class SwarmFollowLeaderWindow : Window {
  private static SwarmFollowLeaderWindow? _instance;
  private readonly SwarmFollowLeaderViewModel _viewModel = new();

  public SwarmFollowLeaderWindow() {
    InitializeComponent();
    DataContext = _viewModel;
    Closed += async (_, _) => {
      await _viewModel.StopAsync();
      _viewModel.Dispose();
      if (ReferenceEquals(_instance, this)) {
        _instance = null;
      }
    };
  }

  public static void OpenWindow() {
    if (_instance is { } existing) {
      existing.Activate();
      return;
    }
    var window = new SwarmFollowLeaderWindow();
    _instance = window;
    Window? owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
