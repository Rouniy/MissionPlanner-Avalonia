using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Views;

public partial class MainWindow : Window {
  public MainWindow() {
    InitializeComponent();

    AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel,
        handledEventsToo: true);
    Closed += (_, _) => (DataContext as System.IDisposable)?.Dispose();
  }

  private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

  private void OnKeyDown(object? sender, KeyEventArgs e) {
    var vm = Vm;
    if (vm == null) {
      return;
    }
    var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
    if (ShouldPreserveFocusedInput(focused as Control, e.Key)) {
      return;
    }
    bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
    switch (e.Key) {
      case Key.D1 when ctrl && vm.ActiveTab == "DATA":
      case Key.D2 when ctrl && vm.ActiveTab == "DATA":
      case Key.D3 when ctrl && vm.ActiveTab == "DATA":
      case Key.D4 when ctrl && vm.ActiveTab == "DATA":
      case Key.D5 when ctrl && vm.ActiveTab == "DATA":
      case Key.D6 when ctrl && vm.ActiveTab == "DATA":
      case Key.D7 when ctrl && vm.ActiveTab == "DATA":
      case Key.D8 when ctrl && vm.ActiveTab == "DATA":
      case Key.D9 when ctrl && vm.ActiveTab == "DATA":
      case Key.D0 when ctrl && vm.ActiveTab == "DATA":
        vm.FlightData.SelectActionTab(ShortcutTabIndex(e.Key));
        break;
      case Key.Space when !ctrl && vm.ActiveTab == "DATA" && vm.FlightData.HasTlog:
        vm.FlightData.PlayPauseTlogCommand.Execute(null);
        break;
      case Key.Subtract when vm.ActiveTab == "DATA" && vm.FlightData.HasTlog:
      case Key.OemMinus when vm.ActiveTab == "DATA" && vm.FlightData.HasTlog:
        vm.FlightData.AdjustTlogSpeed(-1);
        break;
      case Key.Add when vm.ActiveTab == "DATA" && vm.FlightData.HasTlog:
      case Key.OemPlus when vm.ActiveTab == "DATA" && vm.FlightData.HasTlog:
        vm.FlightData.AdjustTlogSpeed(1);
        break;
      case Key.F2:
        vm.NavigateCommand.Execute("DATA");
        break;
      case Key.F3:
        vm.NavigateCommand.Execute("PLAN");
        break;
      case Key.F4:
        vm.NavigateCommand.Execute("CONFIG");
        break;
      case Key.F5:
        vm.GetParamsCommand.Execute(null);
        break;
      case Key.F12:
        vm.Connection.ToggleConnectCommand.Execute(null);
        break;
      case Key.F when ctrl:
        vm.Setup.SelectPage("Developer Tools");
        vm.NavigateCommand.Execute("SETUP");
        break;
      case Key.I when ctrl:
        MAVLinkInspectorWindow.OpenWindow();
        break;
      case Key.G when ctrl:
        SerialOutputNMEAWindow.OpenWindow();
        break;
      case Key.L when ctrl:
        SpectrogramWindow.OpenWindow();
        break;
      case Key.X when ctrl:
        MapCacheWindow.OpenWindow();
        break;
      case Key.J when ctrl:
        DeviceOperationsWindow.OpenWindow();
        break;
      case Key.W when ctrl:
        PropagationSettingsWindow.OpenWindow();
        break;
      case Key.Y when ctrl:
        vm.SaveToEepromCommand.Execute(null);
        break;
      default:
        return;
    }
    e.Handled = true;
  }

  internal static int ShortcutTabIndex(Key key) =>
      key == Key.D0 ? 9 : (int)key - (int)Key.D1;

  internal static bool ShouldPreserveFocusedInput(Control? focused, Key key) {
    for (Control? control = focused; control != null; control = control.Parent as Control) {
      if (control.Classes.Contains("ssh-terminal-input")) {
        return true;
      }
    }
    if (key is Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F12) {
      return false;
    }
    for (Control? control = focused; control != null; control = control.Parent as Control) {
      if (control is TextBox or NumericUpDown or ComboBox or DataGrid or Slider) {
        return true;
      }
      if (key == Key.Space && control is Button) {
        return true;
      }
    }
    return false;
  }

  private void OnFullScreen(object? sender, RoutedEventArgs e) {
    if (WindowState == WindowState.FullScreen) {
      WindowState = WindowState.Normal;
      SystemDecorations = SystemDecorations.Full;
    } else {
      SystemDecorations = SystemDecorations.None;
      WindowState = WindowState.FullScreen;
    }
  }

  private void OnHeaderPointerEntered(object? sender, PointerEventArgs e) {
    if (DataContext is MainWindowViewModel vm) {
      vm.HeaderHovered = true;
    }
  }

  private void OnHeaderPointerExited(object? sender, PointerEventArgs e) {
    if (DataContext is MainWindowViewModel vm) {
      vm.HeaderHovered = false;
    }
  }

  private void OnToggleReadonly(object? sender, RoutedEventArgs e) {
    Vm?.Connection.ReadOnly = !Vm.Connection.ReadOnly;
  }

  private void OnLinkStats(object? sender, RoutedEventArgs e) => LinkStatsWindow.OpenWindow();

  private void OnConnectionOptions(object? sender, RoutedEventArgs e) =>
      ConnectionOptionsWindow.OpenWindow();

  private void OnDownloadLogs(object? sender, RoutedEventArgs e) => LogDownloadWindow.OpenWindow();

  private void OnMavlinkLogConvert(object? sender, RoutedEventArgs e) => MavlinkLogWindow.OpenWindow();

  private void OnMapCache(object? sender, RoutedEventArgs e) => MapCacheWindow.OpenWindow();

  private void OnDeviceOperations(object? sender, RoutedEventArgs e) =>
      DeviceOperationsWindow.OpenWindow();

  private void OnAdvancedTools(object? sender, RoutedEventArgs e) {
    if (Vm is { } vm) {
      vm.Setup.SelectPage("Developer Tools");
      vm.NavigateCommand.Execute("SETUP");
    }
  }

  private void OnMavlinkInspector(object? sender, RoutedEventArgs e) =>
      MAVLinkInspectorWindow.OpenWindow();

  private void OnMavlinkMirror(object? sender, RoutedEventArgs e) =>
      SerialPassThroughWindow.OpenWindow();

  private void OnNmeaOutput(object? sender, RoutedEventArgs e) =>
      SerialOutputNMEAWindow.OpenWindow();

  private void OnCotOutput(object? sender, RoutedEventArgs e) =>
      SerialOutputCotWindow.OpenWindow();

  private void OnSpectrogram(object? sender, RoutedEventArgs e) => SpectrogramWindow.OpenWindow();

  private void OnPropagationSettings(object? sender, RoutedEventArgs e) =>
      PropagationSettingsWindow.OpenWindow();
}
