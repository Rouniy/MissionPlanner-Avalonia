using Avalonia.Controls;
using Avalonia.Interactivity;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class PluginManagerWindow : Window {
  private static PluginManagerWindow? _instance;
  private readonly PluginManagerViewModel _viewModel = new();

  public PluginManagerWindow() {
    InitializeComponent();
    DataContext = _viewModel;
    Closed += (_, _) => {
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
    var window = new PluginManagerWindow();
    _instance = window;
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
