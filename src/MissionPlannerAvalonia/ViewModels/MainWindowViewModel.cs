using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MissionPlannerAvalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, System.IDisposable {
  public ConnectionViewModel Connection { get; } = new();

  public FlightDataViewModel FlightData { get; } = new();
  public FlightPlannerViewModel FlightPlanner { get; } = new();
  public SetupViewModel Setup { get; } = new();
  [System.Obsolete]
  public ConfigViewModel Config { get; } = new();
  public SimulationViewModel Simulation { get; }
  public HelpViewModel Help { get; } = new();

  [ObservableProperty]
  private ViewModelBase _currentScreen;

  [ObservableProperty]
  private string _activeTab = "DATA";

  [ObservableProperty]
  private string _windowTitle = Services.AppVersion.Title;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(HeaderHeight))]
  private bool _menuAutoHide;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(HeaderHeight))]
  private bool _headerHovered;

  public double HeaderHeight => HeaderHeightFor(MenuAutoHide, HeaderHovered);

  public MainWindowViewModel() {
    Simulation = new SimulationViewModel(Connection);
    _currentScreen = FlightData;
    _menuAutoHide = MissionPlanner.Utilities.Settings.Instance.GetBoolean("menu_autohide", false);

    Connection.Connected += OnConnected;
    AppState.ConnectionChanged += OnConnectionChanged;
    FlightData.RequestFlightPlanner += OnFlightPlannerRequested;

    Simulation.RequestFlightData += () =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = Navigate("DATA"));
    _ = Services.KIndexService.RefreshAsync();
  }

  private void OnConnectionChanged() =>
      Avalonia.Threading.Dispatcher.UIThread.Post(() => WindowTitle = BuildWindowTitle());

  private void OnFlightPlannerRequested() =>
      Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = Navigate("PLAN"));

  partial void OnMenuAutoHideChanged(bool value) =>
      MissionPlanner.Utilities.Settings.Instance["menu_autohide"] = value.ToString();

  internal static double HeaderHeightFor(bool autoHide, bool hovered) =>
      autoHide && !hovered ? 7 : 64;

  private static string BuildWindowTitle() => FormatWindowTitle(
      Services.AppVersion.Title,
      AppState.comPort.MAV.VersionString,
      AppState.comPort.MAV.SerialString,
      AppState.IsConnected);

  internal static string FormatWindowTitle(
      string appTitle, string? version, string? serial, bool connected) {
    if (!connected) {
      return appTitle;
    }
    string vehicle = string.Join(" on ", new[] { version, serial }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
    return string.IsNullOrEmpty(vehicle) ? appTitle : $"{appTitle} {vehicle}";
  }

  private void OnConnected() {
    Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadConnectionMissionsAsync());
  }

  private async System.Threading.Tasks.Task LoadConnectionMissionsAsync() {
    if (MissionPlanner.Utilities.Settings.Instance.GetBoolean("loadwpsonconnect", false)) {
      await Navigate("PLAN");
      await FlightPlanner.ReadMissionOnConnectAsync();
    }
    await FlightPlanner.ReadAncillaryOnConnectAsync();
  }

  private bool _passwordUnlocked;

  internal static bool HasValidPasswordHash() {
    var stored = MissionPlanner.Utilities.Settings.Instance["password"];
    if (string.IsNullOrEmpty(stored)) {
      return false;
    }
    try {
      // Upstream Password stores a salted SHA-256 hash: exactly 32 bytes. Anything else can
      // never match any entered password and must be treated as absent.
      return System.Convert.FromBase64String(stored).Length == 32;
    } catch (System.FormatException) {
      return false;
    }
  }

  private async System.Threading.Tasks.Task<bool> UnlockProtectedScreens() {
    var settings = MissionPlanner.Utilities.Settings.Instance;
    if (_passwordUnlocked || !settings.GetBoolean("password_protect", false)) {
      return true;
    }

    // Migration: older builds stored the flag without ever creating a hash. Never lock the
    // user out behind a password that does not exist — offer to set one now instead.
    if (!HasValidPasswordHash()) {
      var newPw = await Services.Dialogs.PasswordInputBox("Password Protect",
          "Password protection is enabled but no password is stored.\n"
          + "Enter a new password, or cancel to disable protection.");
      if (string.IsNullOrEmpty(newPw)) {
        settings["password_protect"] = false.ToString();
        _passwordUnlocked = true;
        return true;
      }
      MissionPlanner.Utilities.Password.EnterPassword(newPw);
      _passwordUnlocked = true;
      return true;
    }

    var pw = await Services.Dialogs.PasswordInputBox("Password", "Enter the settings password");
    if (pw == null) {
      return false;
    }
    bool valid;
    try {
      valid = MissionPlanner.Utilities.Password.ValidatePassword(pw);
    } catch {
      valid = false;
    }
    if (!valid) {
      await Services.Dialogs.Alert("Password", "Incorrect password.");
      return false;
    }
    _passwordUnlocked = true;
    return true;
  }

  [RelayCommand]
  private async System.Threading.Tasks.Task Navigate(string target) {
    if ((target == "SETUP" || target == "CONFIG") && !await UnlockProtectedScreens()) {
      return;
    }
    ActiveTab = target;
    var nextScreen = target switch {
      "DATA" => FlightData,
      "PLAN" => FlightPlanner,
      "SETUP" => Setup,
      "CONFIG" => Config,
      "SIMULATION" => Simulation,
      "HELP" => Help,
      _ => CurrentScreen,
    };
    if (!ReferenceEquals(CurrentScreen, nextScreen)) {
      if (CurrentScreen is IDeactivationAware lifecycle) {
        lifecycle.Deactivate();
      }
      CurrentScreen = nextScreen;
    }
  }

  [RelayCommand]
  private void OpenArduPilotSite() =>
      Services.Dialogs.OpenUrl("https://ardupilot.org/?utm_source=Menu&utm_campaign=MP");

  [RelayCommand]
  private async System.Threading.Tasks.Task GetParams() {
    if (AppState.comPort.BaseStream?.IsOpen != true) {
      return;
    }
    await System.Threading.Tasks.Task.Run(() => AppState.comPort.getParamList());
  }

  [RelayCommand]
  private async System.Threading.Tasks.Task SaveToEeprom() {
    if (AppState.comPort.BaseStream?.IsOpen != true) {
      return;
    }
    await System.Threading.Tasks.Task.Run(() =>
        AppState.comPort.doCommand(AppState.comPort.MAV.sysid, AppState.comPort.MAV.compid,
            MAVLink.MAV_CMD.PREFLIGHT_STORAGE, 1, 0, 0, 0, 0, 0, 0));
  }

  public void Dispose() {
    Connection.Connected -= OnConnected;
    AppState.ConnectionChanged -= OnConnectionChanged;
    FlightData.RequestFlightPlanner -= OnFlightPlannerRequested;
    Connection.Dispose();
    FlightData.Dispose();
    Setup.Dispose();
#pragma warning disable CS0612 // The CONFIG screen remains part of the current application shell.
    Config.Dispose();
#pragma warning restore CS0612
  }
}
