using System;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;

namespace MissionPlannerAvalonia.Services;

internal enum FlightCommandShortcut {
  Auto,
  Loiter,
  AltHold,
  Stabilize,
  Rtl,
  Takeoff,
  Land,
  MinimumThrottle,
}

internal readonly record struct FlightCommandShortcutInfo(
    string Gesture,
    string Action,
    string? Mode,
    bool RequiresArmed);

internal interface IFlightCommandShortcutSink {
  string? Validate(NmeaVehicleTarget target, FlightCommandShortcut shortcut);
  Task<bool> ExecuteAsync(NmeaVehicleTarget target, FlightCommandShortcut shortcut);
}

#pragma warning disable CS0612 // Upstream mode translation still exposes mavlink_set_mode_t.
internal sealed class MavlinkFlightCommandShortcutSink : IFlightCommandShortcutSink {
  public string? Validate(NmeaVehicleTarget target, FlightCommandShortcut shortcut) {
    if (target.Link.ReadOnly) {
      return "The selected MAVLink connection is read-only.";
    }
    FlightCommandShortcutInfo info = FlightCommandShortcuts.Describe(shortcut);
    if (info.Mode == null) {
      return null;
    }
    var request = new MAVLink.mavlink_set_mode_t();
    return target.Link.translateMode(
        target.SystemId, target.ComponentId, info.Mode, ref request)
        ? null
        : $"Mode {info.Mode} is not available for the selected vehicle.";
  }

  public Task<bool> ExecuteAsync(
      NmeaVehicleTarget target, FlightCommandShortcut shortcut) =>
      Task.Run(() => Execute(target, shortcut));

  private static bool Execute(
      NmeaVehicleTarget target, FlightCommandShortcut shortcut) {
    FlightCommandShortcutInfo info = FlightCommandShortcuts.Describe(shortcut);
    if (info.Mode != null) {
      var request = new MAVLink.mavlink_set_mode_t();
      if (!target.Link.translateMode(
              target.SystemId, target.ComponentId, info.Mode, ref request)) {
        return false;
      }
      target.Link.setMode(target.SystemId, target.ComponentId, request);
      return true;
    }

    return shortcut switch {
      FlightCommandShortcut.Takeoff => target.Link.doCommand(
          target.SystemId, target.ComponentId, MAVLink.MAV_CMD.TAKEOFF,
          0, 0, 0, 0, 0, 0, 2),
      FlightCommandShortcut.Land => target.Link.doCommand(
          target.SystemId, target.ComponentId, MAVLink.MAV_CMD.LAND,
          0, 0, 0, 0, 0, 0, 0),
      FlightCommandShortcut.MinimumThrottle => SendMinimumThrottle(target),
      _ => false,
    };
  }

  private static bool SendMinimumThrottle(NmeaVehicleTarget target) {
    // Preserve the official plugin's single RC override: release every other channel and
    // command channel 3 to 1000 us. The autopilot's RC override timeout remains authoritative.
    target.Link.SendRCOverride(
        target.SystemId, target.ComponentId, 0, 0, 1000, 0, 0, 0, 0, 0);
    return true;
  }
}
#pragma warning restore CS0612

internal static class FlightCommandShortcuts {
  internal const string EnabledSettingKey = "flight_command_shortcuts_enabled";
  internal static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromSeconds(10);

  internal static FlightCommandShortcutInfo Describe(FlightCommandShortcut shortcut) =>
      shortcut switch {
        FlightCommandShortcut.Auto => new("Alt+A", "switch to AUTO", "Auto", false),
        FlightCommandShortcut.Loiter => new("Alt+G", "switch to LOITER", "Loiter", false),
        FlightCommandShortcut.AltHold => new("Alt+U", "switch to ALT HOLD", "AltHold", false),
        FlightCommandShortcut.Stabilize =>
            new("Alt+S", "switch to STABILIZE", "Stabilize", false),
        FlightCommandShortcut.Rtl => new("Alt+H", "switch to RTL", "RTL", false),
        FlightCommandShortcut.Takeoff =>
            new("Alt+T", "send TAKEOFF to 2 m without changing mode", null, true),
        FlightCommandShortcut.Land => new("Alt+L", "send LAND", null, true),
        FlightCommandShortcut.MinimumThrottle => new(
            "Alt+0", "send one RC override with channel 3 at 1000 us", null, true),
        _ => throw new ArgumentOutOfRangeException(nameof(shortcut)),
      };
}

internal sealed class FlightCommandShortcutService {
  private readonly Func<NmeaVehicleTarget?> _captureTarget;
  private readonly IFlightCommandShortcutSink _sink;
  private readonly Func<string, string, string, Task<bool>> _confirm;
  private readonly Func<string, string, Task> _alert;
  private readonly Action<string> _status;
  private readonly TimeProvider _timeProvider;
  private int _executing;

  internal FlightCommandShortcutService(Action<string>? status = null)
      : this(
          () => NmeaVehicleSession.CaptureActive(requireOpen: true),
          new MavlinkFlightCommandShortcutSink(),
          Dialogs.ConfirmDangerous,
          Dialogs.Alert,
          status ?? (_ => { }),
          TimeProvider.System) {
  }

  internal FlightCommandShortcutService(
      Func<NmeaVehicleTarget?> captureTarget,
      IFlightCommandShortcutSink sink,
      Func<string, string, string, Task<bool>> confirm,
      Func<string, string, Task> alert,
      Action<string> status,
      TimeProvider timeProvider) {
    _captureTarget = captureTarget;
    _sink = sink;
    _confirm = confirm;
    _alert = alert;
    _status = status;
    _timeProvider = timeProvider;
  }

  internal async Task ExecuteAsync(FlightCommandShortcut shortcut) {
    FlightCommandShortcutInfo info = FlightCommandShortcuts.Describe(shortcut);
    if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0) {
      _status($"{info.Gesture} ignored because another command shortcut is in progress.");
      return;
    }

    try {
      NmeaVehicleTarget? target = _captureTarget();
      if (target == null) {
        await _alert("Flight Command Shortcut", "No connected vehicle is selected.");
        return;
      }
      string? invalid = ValidateTarget(target, shortcut);
      if (invalid != null) {
        await _alert("Flight Command Shortcut", invalid);
        return;
      }

      MAVState state = target.Link.MAVlist[target.SystemId, target.ComponentId];
      string armed = state.cs.armed ? "ARMED" : "DISARMED";
      string failsafe = state.cs.failsafe
          ? "\n\nWARNING: the vehicle currently reports FAILSAFE."
          : "";
      bool accepted = await _confirm(
          $"Flight Shortcut {info.Gesture}",
          $"{info.Gesture} will {info.Action} on "
          + $"{NmeaVehicleSession.Describe(target)}.\n\n"
          + $"Current state: {armed}; mode {state.cs.mode}."
          + failsafe
          + "\n\nKeyboard flight commands are global. Verify the physical vehicle and keep "
          + "the aircraft clear before sending this command.",
          $"Send {info.Gesture}");
      if (!accepted) {
        _status($"{info.Gesture} command cancelled.");
        return;
      }

      NmeaVehicleTarget? current = _captureTarget();
      if (!NmeaVehicleSession.Matches(target, current)) {
        await _alert(
            "Flight Command Shortcut",
            "The selected modem or vehicle changed; no command was sent.");
        return;
      }
      invalid = ValidateTarget(target, shortcut);
      if (invalid != null) {
        await _alert("Flight Command Shortcut", invalid + " No command was sent.");
        return;
      }

      bool sent = await _sink.ExecuteAsync(target, shortcut);
      if (!sent) {
        await _alert(
            "Flight Command Shortcut", "The vehicle rejected the shortcut command.");
        return;
      }
      _status($"{info.Gesture}: requested {info.Action} on "
          + $"{NmeaVehicleSession.Describe(target)}.");
    } catch (Exception ex) {
      await _alert("Flight Command Shortcut", "Command failed: " + ex.Message);
    } finally {
      Interlocked.Exchange(ref _executing, 0);
    }
  }

  private string? ValidateTarget(
      NmeaVehicleTarget target, FlightCommandShortcut shortcut) {
    MAVState state = target.Link.MAVlist[target.SystemId, target.ComponentId];
    DateTime lastPacket = state.lastvalidpacket;
    if (lastPacket == DateTime.MinValue
        || _timeProvider.GetUtcNow().UtcDateTime - lastPacket
            > FlightCommandShortcuts.MaximumTelemetryAge) {
      return "Recent telemetry was not received from the selected vehicle. "
          + "Reconnect or wait for a fresh MAVLink packet before using a flight shortcut.";
    }
    FlightCommandShortcutInfo info = FlightCommandShortcuts.Describe(shortcut);
    if (info.RequiresArmed && !state.cs.armed) {
      return $"{info.Gesture} is blocked because the selected vehicle is disarmed.";
    }
    return _sink.Validate(target, shortcut);
  }
}
