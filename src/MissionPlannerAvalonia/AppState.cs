using System.Collections.Concurrent;
using MissionPlanner;
using MissionPlanner.Comms;

namespace MissionPlannerAvalonia;

public static class AppState {
  public static MAVLinkInterface comPort { get; }

  public static event System.Action? ConnectionChanged;

  public static void RaiseConnectionChanged() => ConnectionChanged?.Invoke();

  public static bool IsConnected => comPort.BaseStream?.IsOpen == true;

  // Written from comms threads via the CommsBase.Settings callback.
  public static ConcurrentDictionary<string, string> CommsSettings { get; } = new();

  public static Services.ProgressReporter? ActiveConnectReporter { get; set; }

  static AppState() {

    Services.AppPaths.Initialize();

    // Replace upstream WinForms UI hooks before constructing or opening any shared
    // MAVLink/communications component.
    global::System.CustomMessageBox.ShowEvent += Services.Dialogs.ShowUpstreamMessage;
    comPort = new MAVLinkInterface();

    MAVLinkInterface.CreateIProgressReporterDialogue +=
        _ => new Services.ForwardingProgressReporter(ActiveConnectReporter);

    CommsBase.Settings += (name, value, set) => {
      if (set) {
        CommsSettings[name] = value;
        return value;
      }
      return CommsSettings.TryGetValue(name, out var v) ? v : "";
    };

    CommsBase.InputBoxShow += Services.Dialogs.ShowUpstreamInput;

    ApplyUnits();
  }

  public static void ApplyUnits() {
    try {
      var s = MissionPlanner.Utilities.Settings.Instance;

      if (s["distunits"] != null && System.Enum.TryParse<distances>(s["distunits"], out var d)
          && d == distances.Feet) {
        CurrentState.multiplierdist = 3.2808399f;
        CurrentState.DistanceUnit = "ft";
      } else {
        CurrentState.multiplierdist = 1;
        CurrentState.DistanceUnit = "m";
      }

      if (s["altunits"] != null && System.Enum.TryParse<altitudes>(s["altunits"], out var a)
          && a == altitudes.Feet) {
        CurrentState.multiplieralt = 3.2808399f;
        CurrentState.AltUnit = "ft";
      } else {
        CurrentState.multiplieralt = 1;
        CurrentState.AltUnit = "m";
      }

      if (s["speedunits"] != null && System.Enum.TryParse<speeds>(s["speedunits"], out var sp)) {
        switch (sp) {
          case speeds.fps:
            CurrentState.multiplierspeed = 3.2808399f;
            CurrentState.SpeedUnit = "fps";
            break;
          case speeds.kph:
            CurrentState.multiplierspeed = 3.6f;
            CurrentState.SpeedUnit = "kph";
            break;
          case speeds.mph:
            CurrentState.multiplierspeed = 2.23693629f;
            CurrentState.SpeedUnit = "mph";
            break;
          case speeds.knots:
            CurrentState.multiplierspeed = 1.94384449f;
            CurrentState.SpeedUnit = "kts";
            break;
          default:
            CurrentState.multiplierspeed = 1;
            CurrentState.SpeedUnit = "m/s";
            break;
        }
      } else {
        CurrentState.multiplierspeed = 1;
        CurrentState.SpeedUnit = "m/s";
      }
    } catch {

    }
  }
}
