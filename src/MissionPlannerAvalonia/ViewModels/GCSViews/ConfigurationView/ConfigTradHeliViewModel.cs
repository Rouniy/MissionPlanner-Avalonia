using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigTradHeliViewModel : ParamPageBase,
    IActivationAware, IDeactivationAware, IDisposable {
  private bool _suppressSwash;
  private readonly DispatcherTimer _visualizationTimer;
  private HeliInputRange _collectiveRange = new(2200, 800);
  private HeliInputRange _rudderRange = new(2200, 800);
  private (double P0, double P40, double P60, double P100, double Expo) _lastCurve =
      (double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

  [ObservableProperty]
  private bool _swashIsCcpm = true;

  [ObservableProperty]
  private string _servoStatus = "";

  [ObservableProperty]
  private IReadOnlyList<HeliCurvePoint> _stabilizeCurve = [];

  [ObservableProperty]
  private IReadOnlyList<HeliCurvePoint> _acroCurve = [];

  [ObservableProperty]
  private double _collectiveCursorPercent;

  [ObservableProperty]
  private double _collectiveInput = 1500;

  [ObservableProperty]
  private double _rudderInput = 1500;

  [ObservableProperty]
  private double _collectiveObservedMinimum = 1500;

  [ObservableProperty]
  private double _collectiveObservedMaximum = 1500;

  [ObservableProperty]
  private double _rudderObservedMinimum = 1500;

  [ObservableProperty]
  private double _rudderObservedMaximum = 1500;

  [ObservableProperty]
  private string _collectiveRangeText = "Collective range: waiting for manual mode";

  [ObservableProperty]
  private string _rudderRangeText = "Rudder range: waiting for manual mode";

  [ObservableProperty]
  private bool _manualServoActive;

  [ObservableProperty]
  private double _servo1Position;

  [ObservableProperty]
  private double _servo2Position;

  [ObservableProperty]
  private double _servo3Position;

  public ConfigTradHeliViewModel() {
    Title = "Heli Setup";
    Intro = "Traditional helicopter swashplate and rotor speed setup. Remove blades before testing servos.";
    Setup();
    ReadSwash();
    _visualizationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    _visualizationTimer.Tick += (_, _) => PumpVisualization();
    PumpVisualization();
  }

  protected override void OnRefreshed() {
    Fields.Clear();
    Setup();
    ReadSwash();
    _lastCurve = (double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
    PumpVisualization();
  }

  private string Pick(params string[] names) {
    foreach (var n in names) {
      if (comPort.MAV.param.ContainsKey(n)) {
        return n;
      }
    }
    return names[0];
  }

  private void Setup() {
    F("H_PHANG");
    F("ATC_PIRO_COMP", "bool");
    F("H_SV_TEST");
    F("ATC_HOVR_ROL_TRM");
    F("H_CYC_MAX");

    F("H_RSC_CRITICAL");
    F(Pick("H_RSC_MAX", "H_RSC_PWM_MAX"));
    F(Pick("H_RSC_MIN", "H_RSC_PWM_MIN"));
    F(Pick("H_RSC_REV", "H_RSC_PWM_REV"));
    F("H_RSC_POWER_HIGH");
    F("H_RSC_POWER_LOW");
    F("H_RSC_IDLE");

    F(Pick("IM_STAB_COL_1", "IM_STB_COL_1"));
    F(Pick("IM_STAB_COL_2", "IM_STB_COL_2"));
    F(Pick("IM_STAB_COL_3", "IM_STB_COL_3"));
    F(Pick("IM_STAB_COL_4", "IM_STB_COL_4"));
    F("IM_ACRO_COL_EXP");

    F("H_TAIL_TYPE", "combo");
    F("H_TAIL_SPEED");
    F("H_LAND_COL_MIN");
    F("H_COLYAW");
    F("H_RSC_RAMP_TIME");
    F("H_RSC_RUNUP_TIME");
    F("H_RSC_MODE", "combo");
    F("H_RSC_SETPOINT");
    F("H_GYR_GAIN");

    F("H_COL_MIN");
    F("H_COL_MID");
    F("H_COL_MAX");
    F(Pick("HS4_MIN", "SERVO4_MIN"));
    F(Pick("HS4_MAX", "SERVO4_MAX"));

    F("H_SV1_POS");
    F("H_SV2_POS");
    F("H_SV3_POS");

    F(Pick("HS1_REV", "H_SV1_REV", "SERVO1_REVERSED"));
    F(Pick("HS2_REV", "H_SV2_REV", "SERVO2_REVERSED"));
    F(Pick("HS3_REV", "H_SV3_REV", "SERVO3_REVERSED"));
    F(Pick("HS4_REV", "H_SV4_REV", "SERVO4_REVERSED"));
    F("H_FLYBAR_MODE", "combo");

    F(Pick("HS1_TRIM", "H_SV1_TRIM", "SERVO1_TRIM"));
    F(Pick("HS2_TRIM", "H_SV2_TRIM", "SERVO2_TRIM"));
    F(Pick("HS3_TRIM", "H_SV3_TRIM", "SERVO3_TRIM"));
    F(Pick("HS4_TRIM", "H_SV4_TRIM", "SERVO4_TRIM"));
  }

  private void ReadSwash() {
    _suppressSwash = true;
    if (comPort.MAV.param.ContainsKey("H_SWASH_TYPE")) {
      SwashIsCcpm = (int)Math.Round(comPort.MAV.param["H_SWASH_TYPE"].Value) == 0;
    }
    _suppressSwash = false;
  }

  public void Activate() {
    ResetObservedRanges();
    PumpVisualization();
    _visualizationTimer.Start();
  }

  public void Deactivate() => _visualizationTimer.Stop();

  public void Dispose() => _visualizationTimer.Stop();

  private void ResetObservedRanges() {
    _collectiveRange = new HeliInputRange(2200, 800);
    _rudderRange = new HeliInputRange(2200, 800);
    CollectiveRangeText = "Collective range: waiting for manual mode";
    RudderRangeText = "Rudder range: waiting for manual mode";
  }

  private void PumpVisualization() {
    double point0 = ReadParameter("IM_STAB_COL_1", "IM_STB_COL_1") ?? 0;
    double point40 = ReadParameter("IM_STAB_COL_2", "IM_STB_COL_2") ?? 400;
    double point60 = ReadParameter("IM_STAB_COL_3", "IM_STB_COL_3") ?? 600;
    double point100 = ReadParameter("IM_STAB_COL_4", "IM_STB_COL_4") ?? 1000;
    double expo = ReadParameter("IM_ACRO_COL_EXP") ?? 0;
    var curve = (point0, point40, point60, point100, expo);
    if (curve != _lastCurve) {
      _lastCurve = curve;
      StabilizeCurve = HeliVisualization.BuildStabilizeCurve(
          point0, point40, point60, point100);
      AcroCurve = HeliVisualization.BuildAcroCurve(expo);
    }

    var state = comPort.MAV.cs;
    CollectiveInput = state.ch3in;
    RudderInput = state.ch4in;
    double collectiveMinimum = ReadParameter("H_COL_MIN") ?? 1000;
    double collectiveMaximum = ReadParameter("H_COL_MAX") ?? 2000;
    CollectiveCursorPercent = HeliVisualization.MapCollectiveCursor(
        state.ch6out, collectiveMinimum, collectiveMaximum);

    ManualServoActive = (ReadParameter("H_SV_MAN") ?? 0) != 0;
    _collectiveRange = HeliVisualization.CaptureRange(
        _collectiveRange, CollectiveInput, ManualServoActive);
    _rudderRange = HeliVisualization.CaptureRange(
        _rudderRange, RudderInput, ManualServoActive);
    ApplyObservedRanges();

    Servo1Position = ReadParameter("H_SV1_POS") ?? 0;
    Servo2Position = ReadParameter("H_SV2_POS") ?? 0;
    Servo3Position = ReadParameter("H_SV3_POS") ?? 0;
  }

  private void ApplyObservedRanges() {
    if (_collectiveRange.HasSamples) {
      CollectiveObservedMinimum = _collectiveRange.Minimum;
      CollectiveObservedMaximum = _collectiveRange.Maximum;
      CollectiveRangeText = $"Collective observed: {_collectiveRange.Minimum:0}–{_collectiveRange.Maximum:0} µs";
    }
    if (_rudderRange.HasSamples) {
      RudderObservedMinimum = _rudderRange.Minimum;
      RudderObservedMaximum = _rudderRange.Maximum;
      RudderRangeText = $"Rudder observed: {_rudderRange.Minimum:0}–{_rudderRange.Maximum:0} µs";
    }
  }

  private double? ReadParameter(params string[] names) {
    foreach (string name in names) {
      if (comPort.MAV.param.ContainsKey(name)) {
        double value = comPort.MAV.param[name].Value;
        return double.IsFinite(value) ? value : null;
      }
    }
    return null;
  }

  [System.Obsolete]
  partial void OnSwashIsCcpmChanged(bool value) {
    if (_suppressSwash) {
      return;
    }
    WriteSwash(value ? 0 : 1);
  }

  [System.Obsolete]
  private async void WriteSwash(double value) {
    if (comPort.BaseStream?.IsOpen != true) {
      ServoStatus = "offline";
      return;
    }
    try {
      var ok = await Task.Run(() => comPort.setParam("H_SWASH_TYPE", value));
      ServoStatus = ok ? "H_SWASH_TYPE set" : "Set H_SWASH_TYPE Failed";
    } catch {
      ServoStatus = "Set H_SWASH_TYPE Failed";
    }
  }

  [RelayCommand]
  [System.Obsolete]
  private async Task SetServoMan(string mode) {
    if (!int.TryParse(mode, out var v)) {
      return;
    }
    if (comPort.BaseStream?.IsOpen != true) {
      ServoStatus = "offline";
      return;
    }
    try {
      var ok = await Task.Run(() => comPort.setParam("H_SV_MAN", v));
      ServoStatus = ok ? "H_SV_MAN=" + v : "Set H_SV_MAN Failed";
    } catch {
      ServoStatus = "Set H_SV_MAN Failed";
    }
  }
}
