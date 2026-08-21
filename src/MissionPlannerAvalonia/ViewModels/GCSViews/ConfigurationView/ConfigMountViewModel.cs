using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigMountViewModel : ViewModelBase, IActivationAware {
  internal readonly MAVLinkInterface _comPort = AppState.comPort;
  private string _paramHead = "MNT_";

  private const string _relay = "Relay";
  private const string _transistor = "Transistor";
  private const string _disable = "Disable";

  private bool _startup = true;
  private string _typeParam = "MNT_TYPE";
  private readonly SemaphoreSlim _writeGate = new(1, 1);

  public ConfigMountViewModel() {
    var parameters = SnapshotParameters();
    _paramHead = ResolveMountParameterPrefix(parameters.Keys);
    _typeParam = _paramHead + "TYPE";
    var family = parameters.ContainsKey("SERVO1_MIN") ? "SERVO" : "RC";
    var channels = BuildChannelNames(family);

    Tilt = new MountAxisViewModel(
        this, "Tilt", 7, -90, 0, 0, 90, channels);
    Roll = new MountAxisViewModel(
        this, "Roll", 8, -90, 0, 0, 90, channels);
    Pan = new MountAxisViewModel(
        this, "Pan", 6, -180, 0, 0, 180, channels);

    Reload();
  }

  public MountAxisViewModel Tilt { get; }
  public MountAxisViewModel Roll { get; }
  public MountAxisViewModel Pan { get; }

  public ObservableCollection<string> ShutterChannels { get; } = new();
  public ObservableCollection<ParamOption> MountTypes { get; } = new();

  [ObservableProperty]
  private bool _hasMountParameters;

  [ObservableProperty]
  private bool _hasShutterParameters;

  [ObservableProperty]
  private string _selectedShutter = "";

  [ObservableProperty]
  private ParamOption? _selectedMountType;

  [ObservableProperty]
  private double _neutralX;

  [ObservableProperty]
  private double _neutralY;

  [ObservableProperty]
  private double _neutralZ;

  [ObservableProperty]
  private double _retractX;

  [ObservableProperty]
  private double _retractY;

  [ObservableProperty]
  private double _retractZ;

  [ObservableProperty]
  private bool _hasStabilizeTilt;

  [ObservableProperty]
  private bool _stabilizeTilt;

  [ObservableProperty]
  private bool _hasStabilizeRoll;

  [ObservableProperty]
  private bool _stabilizeRoll;

  [ObservableProperty]
  private bool _hasShutterChannelLimits;

  [ObservableProperty]
  private double _shutterMin = 1000;

  [ObservableProperty]
  private double _shutterMax = 2000;

  [ObservableProperty]
  private bool _hasCameraServoOn;

  [ObservableProperty]
  private double _cameraServoOn = 1900;

  [ObservableProperty]
  private bool _hasCameraServoOff;

  [ObservableProperty]
  private bool _hasCameraServoSettings;

  [ObservableProperty]
  private double _cameraServoOff = 1100;

  [ObservableProperty]
  private bool _hasCameraDuration;

  [ObservableProperty]
  private double _cameraDuration = 10;

  [ObservableProperty]
  private string _status = "";

  public void Activate() => Reload();

  private void Reload() {
    _startup = true;

    var parameters = SnapshotParameters();
    _paramHead = ResolveMountParameterPrefix(parameters.Keys);
    _typeParam = _paramHead + "TYPE";
    HasMountParameters = parameters.Keys.Any(
        name => name.StartsWith(_paramHead, StringComparison.Ordinal));
    HasShutterParameters = parameters.ContainsKey("CAM_TRIGG_TYPE");

    string channelFamily = parameters.ContainsKey("SERVO1_MIN") ? "SERVO" : "RC";
    var channels = BuildChannelNames(channelFamily)
        .Where(channel => parameters.ContainsKey(channel + "_FUNCTION"))
        .ToArray();
    Tilt.ConfigureParameterPrefix(_paramHead);
    Roll.ConfigureParameterPrefix(_paramHead);
    Pan.ConfigureParameterPrefix(_paramHead);
    Tilt.ReplaceFunctions(channels);
    Roll.ReplaceFunctions(channels);
    Pan.ReplaceFunctions(channels);

    ShutterChannels.Clear();
    ShutterChannels.Add(_disable);
    foreach (var channel in channels) {
      ShutterChannels.Add(channel);
    }
    ShutterChannels.Add(_relay);
    ShutterChannels.Add(_transistor);

    MountTypes.Clear();
    double? typeValue = GetParam(_typeParam);
    foreach (var option in BuildMountTypeOptions(
        _paramHead, SafeOptions(_typeParam), typeValue)) {
      MountTypes.Add(option);
    }

    Tilt.SetSelectedFunctionSilent(_disable);
    Roll.SetSelectedFunctionSilent(_disable);
    Pan.SetSelectedFunctionSilent(_disable);
    SelectedShutter = _disable;
    foreach (var name in parameters.Keys) {
      if (!name.EndsWith("_FUNCTION")) {
        continue;
      }
      var ch = name.Replace("_FUNCTION", "");
      switch ((int)Math.Round(parameters[name])) {
        case 6:
          Pan.SetSelectedFunctionSilent(ch);
          break;
        case 7:
          Tilt.SetSelectedFunctionSilent(ch);
          break;
        case 8:
          Roll.SetSelectedFunctionSilent(ch);
          break;
        case 10:
          SelectedShutter = ch;
          break;
      }
    }

    var trigg = GetParam("CAM_TRIGG_TYPE");
    if (trigg.HasValue) {
      if ((int)Math.Round(trigg.Value) == 1) {
        SelectedShutter = _relay;
      } else if ((int)Math.Round(trigg.Value) == 4) {
        SelectedShutter = _transistor;
      }
    }

    Tilt.ReloadFromParams();
    Roll.ReloadFromParams();
    Pan.ReloadFromParams();

    SelectedMountType = MountTypes.FirstOrDefault(
        option => option.Value == (int)Math.Round(typeValue ?? -1));
    NeutralX = GetParam(_paramHead + "NEUTRAL_X") ?? 0;
    NeutralY = GetParam(_paramHead + "NEUTRAL_Y") ?? 0;
    NeutralZ = GetParam(_paramHead + "NEUTRAL_Z") ?? 0;
    RetractX = GetParam(_paramHead + "RETRACT_X") ?? 0;
    RetractY = GetParam(_paramHead + "RETRACT_Y") ?? 0;
    RetractZ = GetParam(_paramHead + "RETRACT_Z") ?? 0;

    string stabilizeTiltParam = _paramHead + "STAB_TILT";
    HasStabilizeTilt = HasParam(stabilizeTiltParam);
    StabilizeTilt = (GetParam(stabilizeTiltParam) ?? 0) != 0;
    string stabilizeRollParam = _paramHead + "STAB_ROLL";
    HasStabilizeRoll = HasParam(stabilizeRollParam);
    StabilizeRoll = (GetParam(stabilizeRollParam) ?? 0) != 0;
    ReloadShutterNumerics();

    Status = HasMountParameters
        ? HasShutterParameters ? "" : "Camera trigger parameters are not available on this vehicle."
        : HasShutterParameters
            ? "Mount parameters are not available; only shutter settings can be changed."
            : "No supported mount or camera trigger parameters are available.";

    _startup = false;
  }

  [Obsolete]
  partial void OnSelectedMountTypeChanged(ParamOption? value) {
    if (_startup || value is null) {
      return;
    }
    SetParam(_typeParam, value.Value);
  }

  [Obsolete]
  partial void OnSelectedShutterChanged(string value) {
    AxisSelectionChanged();
    ReloadShutterNumerics();
  }

  [Obsolete]
  partial void OnNeutralXChanged(double value) => WriteNum(_paramHead + "NEUTRAL_X", value);

  [Obsolete]
  partial void OnNeutralYChanged(double value) => WriteNum(_paramHead + "NEUTRAL_Y", value);

  [Obsolete]
  partial void OnNeutralZChanged(double value) => WriteNum(_paramHead + "NEUTRAL_Z", value);

  [Obsolete]
  partial void OnRetractXChanged(double value) => WriteNum(_paramHead + "RETRACT_X", value);

  [Obsolete]
  partial void OnRetractYChanged(double value) => WriteNum(_paramHead + "RETRACT_Y", value);

  [Obsolete]
  partial void OnRetractZChanged(double value) => WriteNum(_paramHead + "RETRACT_Z", value);

  [Obsolete]
  partial void OnStabilizeTiltChanged(bool value) =>
      WriteNum(_paramHead + "STAB_TILT", value ? 1 : 0);

  [Obsolete]
  partial void OnStabilizeRollChanged(bool value) =>
      WriteNum(_paramHead + "STAB_ROLL", value ? 1 : 0);

  [Obsolete]
  partial void OnShutterMinChanged(double value) => WriteShutterChannelParam("_MIN", value);

  [Obsolete]
  partial void OnShutterMaxChanged(double value) => WriteShutterChannelParam("_MAX", value);

  [Obsolete]
  partial void OnCameraServoOnChanged(double value) => WriteNum("CAM_SERVO_ON", value);

  [Obsolete]
  partial void OnCameraServoOffChanged(double value) => WriteNum("CAM_SERVO_OFF", value);

  [Obsolete]
  partial void OnCameraDurationChanged(double value) => WriteNum("CAM_DURATION", value);

  [Obsolete]
  private void WriteShutterChannelParam(string suffix, double value) {
    if (_startup || !IsServoChannel(SelectedShutter)) {
      return;
    }
    SetParam(SelectedShutter + suffix, value);
  }

  [Obsolete]
  private void WriteNum(string name, double value) {
    if (_startup) {
      return;
    }
    SetParam(name, value);
  }

  [Obsolete]
  internal void AxisSelectionChanged() {
    if (_startup) {
      return;
    }

    try {
      EnsureDisabled(6, Pan.SelectedFunction);
      EnsureDisabled(7, Tilt.SelectedFunction);
      EnsureDisabled(8, Roll.SelectedFunction);

      if (HasParam("MNT_MODE")) {
        SetParam("MNT_MODE", 3);
      }

      UpdateShutter();
      Tilt.ApplyFunction();
      Roll.ApplyFunction();
      Pan.ApplyFunction();
    } catch (Exception ex) {
      Status = "Failed to set param: " + ex.Message;
    }
  }

  [Obsolete]
  private void UpdateShutter() {
    if (!HasShutterParameters) {
      return;
    }
    var sel = SelectedShutter;
    if (string.IsNullOrEmpty(sel)) {
      return;
    }

    if (sel == _disable) {
      SetParam("CAM_TRIGG_TYPE", 0);
      EnsureDisabled(10);
    } else if (sel == _relay) {
      EnsureDisabled(10);
      SetParam("CAM_TRIGG_TYPE", 1);
    } else if (sel == _transistor) {
      EnsureDisabled(10);
      SetParam("CAM_TRIGG_TYPE", 4);
    } else {
      EnsureDisabled(10);
      SetParam(sel + "_FUNCTION", 10);
      SetParam("CAM_TRIGG_TYPE", 0);
    }
  }

  private void ReloadShutterNumerics() {
    bool wasStarting = _startup;
    _startup = true;
    try {
      bool channelSelected = IsServoChannel(SelectedShutter);
      HasShutterChannelLimits = channelSelected
          && (HasParam(SelectedShutter + "_MIN") || HasParam(SelectedShutter + "_MAX"));
      ShutterMin = channelSelected ? GetParam(SelectedShutter + "_MIN") ?? 1000 : 1000;
      ShutterMax = channelSelected ? GetParam(SelectedShutter + "_MAX") ?? 2000 : 2000;

      HasCameraServoOn = HasParam("CAM_SERVO_ON");
      CameraServoOn = GetParam("CAM_SERVO_ON") ?? 1900;
      HasCameraServoOff = HasParam("CAM_SERVO_OFF");
      CameraServoOff = GetParam("CAM_SERVO_OFF") ?? 1100;
      HasCameraServoSettings = HasCameraServoOn || HasCameraServoOff;
      HasCameraDuration = HasParam("CAM_DURATION");
      CameraDuration = GetParam("CAM_DURATION") ?? 10;
    } finally {
      _startup = wasStarting;
    }
  }

  private static bool IsServoChannel(string? value) =>
      !string.IsNullOrEmpty(value)
      && value != _disable && value != _relay && value != _transistor;

  [Obsolete]
  internal void EnsureDisabled(int role, string exclude = "") {
    foreach (var ch in ChannelNamesWithFunction()) {
      if (ch == exclude) {
        continue;
      }
      var fn = ch + "_FUNCTION";
      var parameter = _comPort.MAV.param[fn];
      if (parameter != null && (int)Math.Round(parameter.Value) == role) {
        SetParam(fn, 0);
      }
    }
  }

  private IEnumerable<string> ChannelNamesWithFunction() {
    foreach (var name in SnapshotParameters().Keys) {
      if (name.EndsWith("_FUNCTION")) {
        yield return name.Replace("_FUNCTION", "");
      }
    }
  }

  internal bool Online => _comPort.BaseStream?.IsOpen == true;

  internal bool HasParam(string name) => _comPort.MAV.param.ContainsKey(name);

  internal double? GetParam(string name) => _comPort.MAV.param[name]?.Value;

  private Dictionary<string, double> SnapshotParameters() => _comPort.MAV.param;

  [Obsolete]
  internal async void SetParam(string name, double value) {
    var parameter = _comPort.MAV.param[name];
    if (parameter == null) {
      Status = name + " is not available on this vehicle";
      return;
    }
    if (!Online) {
      parameter.Value = value;
      Status = "offline";
      return;
    }
    await _writeGate.WaitAsync();
    try {
      if (!Online || _comPort.MAV.param[name] == null) {
        Status = name + " is no longer available";
        return;
      }
      var n = name;
      var v = value;
      var ok = await Task.Run(() => _comPort.setParam(n, v, true));
      Status = ok ? name + " set" : name + " write failed";
    } catch (Exception ex) {
      Status = ex.Message;
    } finally {
      _writeGate.Release();
    }
  }

  internal static string ResolveMountParameterPrefix(IEnumerable<string> names) {
    var parameters = names as IReadOnlyCollection<string> ?? names.ToArray();
    if (parameters.Contains("MNT1_TYPE")
        || parameters.Any(name => name.StartsWith("MNT1_", StringComparison.Ordinal))) {
      return "MNT1_";
    }
    return "MNT_";
  }

  internal static (string Minimum, string Maximum, string? Input, double Scale)
      ResolveAxisParameters(string prefix, string axis) {
    if (prefix == "MNT1_") {
      string modernAxis = axis switch {
        "Tilt" => "PITCH",
        "Roll" => "ROLL",
        _ => "YAW",
      };
      return (prefix + modernAxis + "_MIN", prefix + modernAxis + "_MAX", null, 1);
    }

    string legacyAxis = axis switch {
      "Tilt" => "TIL",
      "Roll" => "ROL",
      _ => "PAN",
    };
    return (
        prefix + "ANGMIN_" + legacyAxis,
        prefix + "ANGMAX_" + legacyAxis,
        prefix + "RC_IN_" + axis.ToUpperInvariant(),
        100);
  }

  internal static double ParameterAngleToDegrees(double value, double scale) => value / scale;

  internal static double DegreesToParameterAngle(double value, double scale) => value * scale;

  internal static IReadOnlyList<ParamOption> BuildMountTypeOptions(
      string prefix, IEnumerable<KeyValuePair<int, string>> metadata, double? currentValue) {
    var options = metadata
        .GroupBy(option => option.Key)
        .Select(group => new ParamOption(group.Key, group.First().Value))
        .OrderBy(option => option.Value)
        .ToList();
    if (options.Count == 0) {
      string[] names = prefix == "MNT1_"
          ? [
            "None", "Servo", "3DR Solo", "Alexmos Serial", "SToRM32 MAVLink",
            "SToRM32 Serial", "Gremsy", "Brushless PWM", "Siyi", "Scripting",
            "Xacti", "Viewpro", "Topotek", "CADDX", "XFRobot",
          ]
          : ["None", "Servo", "3DR Solo", "Alexmos Serial", "SToRM32 MAVLink", "SToRM32 Serial"];
      options.AddRange(names.Select((name, value) => new ParamOption(value, name)));
    }
    if (currentValue.HasValue) {
      int current = (int)Math.Round(currentValue.Value);
      if (options.All(option => option.Value != current)) {
        options.Add(new ParamOption(current, $"Current ({current})"));
      }
    }
    return options.OrderBy(option => option.Value).ToArray();
  }

  internal static List<string> BuildChannelNames(string family) {
    var list = new List<string>();
    if (family == "SERVO") {
      for (var i = 1; i <= 14; i++) {
        list.Add("SERVO" + i);
      }
    } else {
      for (var i = 5; i <= 14; i++) {
        list.Add("RC" + i);
      }
    }
    return list;
  }

  private static List<KeyValuePair<int, string>> SafeOptions(string name) {
    try {
      return ParameterMetaDataRepository.GetParameterOptionsInt(
                 name, AppState.comPort.MAV.cs.firmware.ToString())
             ?? new List<KeyValuePair<int, string>>();
    } catch {
      return new List<KeyValuePair<int, string>>();
    }
  }
}

public partial class MountAxisViewModel : ObservableObject {
  private readonly ConfigMountViewModel _parent;
  private readonly int _role;
  private string _angMin = "";
  private string _angMax = "";
  private string? _rcIn;
  private double _angleScale = 100;
  private bool _suppress;
  private readonly double _legacyAngleMinLowerBound;
  private readonly double _legacyAngleMinUpperBound;
  private readonly double _legacyAngleMaxLowerBound;
  private readonly double _legacyAngleMaxUpperBound;

  public MountAxisViewModel(ConfigMountViewModel parent, string header, int role,
      double angleMinLo, double angleMinHi, double angleMaxLo, double angleMaxHi,
      IEnumerable<string> channels) {
    _parent = parent;
    Header = header;
    _role = role;
    _legacyAngleMinLowerBound = angleMinLo;
    _legacyAngleMinUpperBound = angleMinHi;
    _legacyAngleMaxLowerBound = angleMaxLo;
    _legacyAngleMaxUpperBound = angleMaxHi;
    AngleMinLowerBound = angleMinLo;
    AngleMinUpperBound = angleMinHi;
    AngleMaxLowerBound = angleMaxLo;
    AngleMaxUpperBound = angleMaxHi;

    ReplaceFunctions(channels);
    ConfigureParameterPrefix("MNT_");

    InputChannels.Add(new ParamOption(0, "Disable"));
    for (var i = 5; i <= 16; i++) {
      InputChannels.Add(new ParamOption(i, "RC" + i));
    }
  }

  public string Header { get; }
  public ObservableCollection<string> Functions { get; } = new();
  public ObservableCollection<ParamOption> InputChannels { get; } = new();

  public double AngleMinLowerBound { get; private set; }
  public double AngleMinUpperBound { get; private set; }
  public double AngleMaxLowerBound { get; private set; }
  public double AngleMaxUpperBound { get; private set; }

  [ObservableProperty]
  private string _selectedFunction = "";

  [ObservableProperty]
  private double _servoMin = 1000;

  [ObservableProperty]
  private double _servoMax = 2000;

  [ObservableProperty]
  private double _angleMin;

  [ObservableProperty]
  private double _angleMax;

  [ObservableProperty]
  private bool _reverse;

  [ObservableProperty]
  private ParamOption? _selectedInput;

  [ObservableProperty]
  private bool _hasAngleLimits;

  [ObservableProperty]
  private bool _hasInputChannel;

  internal void ConfigureParameterPrefix(string prefix) {
    (_angMin, _angMax, _rcIn, _angleScale) =
        ConfigMountViewModel.ResolveAxisParameters(prefix, Header);
    if (prefix == "MNT1_") {
      double limit = Header == "Tilt" ? 90 : 180;
      AngleMinLowerBound = AngleMaxLowerBound = -limit;
      AngleMinUpperBound = AngleMaxUpperBound = limit;
    } else {
      AngleMinLowerBound = _legacyAngleMinLowerBound;
      AngleMinUpperBound = _legacyAngleMinUpperBound;
      AngleMaxLowerBound = _legacyAngleMaxLowerBound;
      AngleMaxUpperBound = _legacyAngleMaxUpperBound;
    }
    OnPropertyChanged(nameof(AngleMinLowerBound));
    OnPropertyChanged(nameof(AngleMinUpperBound));
    OnPropertyChanged(nameof(AngleMaxLowerBound));
    OnPropertyChanged(nameof(AngleMaxUpperBound));
  }

  internal void ReplaceFunctions(IEnumerable<string> channels) {
    string previous = SelectedFunction;
    _suppress = true;
    Functions.Clear();
    Functions.Add("Disable");
    foreach (var channel in channels) {
      Functions.Add(channel);
    }
    SelectedFunction = Functions.Contains(previous) ? previous : "Disable";
    _suppress = false;
  }

  internal void SetSelectedFunctionSilent(string value) {
    _suppress = true;
    SelectedFunction = value;
    _suppress = false;
  }

  internal void ReloadFromParams() {
    _suppress = true;
    ReloadChannelNumerics();
    HasAngleLimits = _parent.HasParam(_angMin) || _parent.HasParam(_angMax);
    AngleMin = ConfigMountViewModel.ParameterAngleToDegrees(
        _parent.GetParam(_angMin) ?? 0, _angleScale);
    AngleMax = ConfigMountViewModel.ParameterAngleToDegrees(
        _parent.GetParam(_angMax) ?? 0, _angleScale);
    HasInputChannel = _rcIn != null && _parent.HasParam(_rcIn);
    SelectedInput = _rcIn == null
        ? null
        : InputChannels.FirstOrDefault(
            option => option.Value == (int)Math.Round(_parent.GetParam(_rcIn) ?? -1));
    _suppress = false;
  }

  [Obsolete]
  internal void ApplyFunction() {
    if (string.IsNullOrEmpty(SelectedFunction)) {
      return;
    }
    if (SelectedFunction != "Disable") {
      _parent.SetParam(SelectedFunction + "_FUNCTION", _role);
    } else {
      _parent.EnsureDisabled(_role);
    }
    _suppress = true;
    ReloadChannelNumerics();
    _suppress = false;
  }

  private void ReloadChannelNumerics() {
    if (string.IsNullOrEmpty(SelectedFunction) || SelectedFunction == "Disable") {
      return;
    }

    ServoMin = _parent.GetParam(SelectedFunction + "_MIN") ?? 1000;
    OnPropertyChanged(nameof(ServoMin));
    ServoMax = _parent.GetParam(SelectedFunction + "_MAX") ?? 2000;
    OnPropertyChanged(nameof(ServoMax));

    var revName = ReverseParam();
    var val = _parent.GetParam(revName) ?? 0;
    Reverse = revName.EndsWith("_REVERSED") ? val == 1 : val == -1;
    OnPropertyChanged(nameof(Reverse));
  }

  private string ReverseParam() =>
      _parent.HasParam(SelectedFunction + "_REVERSED") ? SelectedFunction + "_REVERSED"
                                                       : SelectedFunction + "_REV";

  [Obsolete]
  partial void OnSelectedFunctionChanged(string value) {
    if (_suppress) {
      return;
    }
    _parent.AxisSelectionChanged();
  }

  [Obsolete]
  partial void OnServoMinChanged(double value) {
    if (_suppress || string.IsNullOrEmpty(SelectedFunction) || SelectedFunction == "Disable") {
      return;
    }
    _parent.SetParam(SelectedFunction + "_MIN", value);
  }

  [Obsolete]
  partial void OnServoMaxChanged(double value) {
    if (_suppress || string.IsNullOrEmpty(SelectedFunction) || SelectedFunction == "Disable") {
      return;
    }
    _parent.SetParam(SelectedFunction + "_MAX", value);
  }

  [Obsolete]
  partial void OnAngleMinChanged(double value) {
    if (_suppress) {
      return;
    }
    _parent.SetParam(
        _angMin, ConfigMountViewModel.DegreesToParameterAngle(value, _angleScale));
  }

  [Obsolete]
  partial void OnAngleMaxChanged(double value) {
    if (_suppress) {
      return;
    }
    _parent.SetParam(
        _angMax, ConfigMountViewModel.DegreesToParameterAngle(value, _angleScale));
  }

  [Obsolete]
  partial void OnReverseChanged(bool value) {
    if (_suppress || string.IsNullOrEmpty(SelectedFunction) || SelectedFunction == "Disable") {
      return;
    }
    var revName = ReverseParam();
    var v = revName.EndsWith("_REVERSED") ? (value ? 1 : 0) : (value ? -1 : 1);
    _parent.SetParam(revName, v);
  }

  [Obsolete]
  partial void OnSelectedInputChanged(ParamOption? value) {
    if (_suppress || value is null || _rcIn == null) {
      return;
    }
    _parent.SetParam(_rcIn, value.Value);
  }
}
