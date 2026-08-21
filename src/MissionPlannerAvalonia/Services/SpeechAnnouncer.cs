using System;
using Avalonia.Threading;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

// Arm/disarm and battery voice alerts. Mode and waypoint announcements come from the upstream
// CurrentState handlers once CurrentState.Speech is assigned; this covers the loops that live in
// upstream MainV2 and therefore have no cross-platform equivalent. At most one announcement is
// issued per tick, and interval countdowns restart on every new connection.
public static class SpeechAnnouncer {
  private static DispatcherTimer? _timer;
  private static bool _wasConnected;
  private static bool _lastArmed;
  private static DateTime _lastBattery = DateTime.MinValue;
  private static DateTime _lastCustom = DateTime.MinValue;
  private static DateTime _lastLowSpeed = DateTime.MinValue;
  private static DateTime _lastAltWarning = DateTime.MinValue;
  private static DateTime _connectedAt = DateTime.MinValue;
  private static DateTime _lastNoData = DateTime.MinValue;
  private static string _lastMessageHigh = "";
  private static double _altMax;

  public static void Start() {
    if (_timer != null) {
      return;
    }
    _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    _timer.Tick += (_, _) => Tick();
    _timer.Start();
  }

  public static void Stop() {
    _timer?.Stop();
    _timer = null;
  }

  private static void ResetCountdowns() {
    // Interval countdowns begin at connect, matching upstream — the periodic announcements
    // must not fire on the first tick after connecting.
    var now = DateTime.UtcNow;
    _lastCustom = now;
    _lastBattery = now;
    _lastLowSpeed = now;
    _lastAltWarning = now;
    _connectedAt = now;
    _lastNoData = now;
    _lastMessageHigh = "";
    _lastArmed = false;
    _altMax = 0;
  }

  private static void Tick() {
    bool connected = AppState.IsConnected;
    if (connected && !_wasConnected) {
      ResetCountdowns();
    }
    _wasConnected = connected;

    if (!Speech.Enabled || !connected) {
      return;
    }
    // Match upstream: hold announcements while an utterance is still playing or queued, and
    // speak at most one alert per tick so several types cannot pile up at once.
    if (!Speech.Adapter.IsReady) {
      return;
    }

    var mav = AppState.comPort.MAV;
    var cs = mav?.cs;
    if (mav == null || cs == null) {
      return;
    }
    var s = Settings.Instance;

    bool armed = cs.armed;
    if (armed != _lastArmed) {
      _lastArmed = armed;
      if (s.GetBoolean("speecharmenabled")) {
        var template = armed
            ? s["speecharm"] ?? "Armed"
            : s["speechdisarm"] ?? "Disarmed";
        Speak(mav, template);
        return;
      }
    }

    // Upstream keeps arm/disarm announcements outside the armed-only gate, but all regular
    // status, battery, mode and waypoint speech obeys this setting.
    if (s.GetBoolean("speech_armed_only", false) && !armed) {
      return;
    }

    var now = DateTime.UtcNow;
    if (ShouldWarnNoData(
            now, mav.lastvalidpacket, _connectedAt, _lastNoData, armed)) {
      string message = NoDataMessage(now, mav.lastvalidpacket);
      cs.messageHigh = message;
      _lastMessageHigh = message;
      _lastNoData = now;
      Speech.Speak(message);
      return;
    }

    string messageHigh = cs.messageHigh ?? "";
    if (ShouldSpeakHighMessage(messageHigh, _lastMessageHigh)) {
      _lastMessageHigh = messageHigh;
      Speech.Speak(messageHigh);
      return;
    }
    if (!string.IsNullOrWhiteSpace(messageHigh)) {
      _lastMessageHigh = messageHigh;
    }

    if (s.GetBoolean("speechbatteryenabled") &&
        (now - _lastBattery).TotalSeconds > 30) {
      float warnvolt = s.GetFloat("speechbatteryvolt", 9.6f);
      float warnpercent = s.GetFloat("speechbatterypercent", 20f);
      bool lowVolt = cs.battery_voltage <= warnvolt && cs.battery_voltage >= 5.0;
      bool lowPercent = cs.battery_remaining < warnpercent && cs.battery_voltage >= 5.0 &&
                        cs.battery_remaining != 0.0;
      if (lowVolt || lowPercent) {
        _lastBattery = now;
        Speak(mav, s["speechbattery"] ?? "WARNING, Battery at {batv} Volt, {batp} percent");
        return;
      }
    }

    if (s.GetBoolean("speechcustomenabled") &&
        (now - _lastCustom).TotalSeconds > 30) {
      _lastCustom = now;
      Speak(mav, s["speechcustom"] ?? "Heading to Waypoint {wpn}");
      return;
    }

    // Altitude warning, matching upstream: speak while below the threshold after the vehicle
    // has been above it. speechaltheight is stored in raw metres.
    double altRaw = cs.alt / MissionPlanner.CurrentState.multiplieralt;
    if (!armed) {
      _altMax = 0;
    } else {
      _altMax = Math.Max(_altMax, altRaw);
      float warnalt = s.GetFloat("speechaltheight", float.MaxValue);
      if (s.GetBoolean("speechaltenabled") && altRaw != 0 && altRaw <= warnalt &&
          _altMax > warnalt && (now - _lastAltWarning).TotalSeconds > 10) {
        _lastAltWarning = now;
        Speak(mav, s["speechalt"] ?? "WARNING, low altitude {alt}");
        return;
      }
    }

    if (s.GetBoolean("speechlowspeedenabled") && armed &&
        (now - _lastLowSpeed).TotalSeconds > 10) {
      float warnAirspeed = s.GetFloat("speechlowairspeedtrigger");
      float warnGroundspeed = s.GetFloat("speechlowgroundspeedtrigger");
      if (cs.airspeed < warnAirspeed) {
        _lastLowSpeed = now;
        Speak(mav, s["speechlowairspeed"] ?? "Low Air Speed {asp}");
      } else if (cs.groundspeed < warnGroundspeed) {
        _lastLowSpeed = now;
        Speak(mav, s["speechlowgroundspeed"] ?? "Low Ground Speed {gsp}");
      }
    }
  }

  internal static bool ShouldWarnNoData(
      DateTime now, DateTime lastValidPacket, DateTime connectedAt,
      DateTime lastWarning, bool armed) {
    if (!armed || connectedAt == DateTime.MinValue || lastValidPacket == DateTime.MinValue) {
      return false;
    }
    return now > connectedAt.AddSeconds(30)
        && now > lastValidPacket.AddSeconds(3)
        && now > lastWarning.AddSeconds(5);
  }

  internal static string NoDataMessage(DateTime now, DateTime lastValidPacket) =>
      $"WARNING No Data for {(int)Math.Max(0, (now - lastValidPacket).TotalSeconds)} Seconds";

  internal static bool ShouldSpeakHighMessage(string? message, string? lastMessage) =>
      !string.IsNullOrWhiteSpace(message)
      && !string.Equals(message, lastMessage, StringComparison.Ordinal)
      && !message.StartsWith("PX4v2 ", StringComparison.Ordinal)
      && !message.StartsWith("PreArm:", StringComparison.Ordinal);

  private static void Speak(MissionPlanner.MAVState mav, string template) {
    try {
      Speech.Speak(Common.speechConversion(mav, template));
    } catch {

    }
  }
}
