using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class AntennaTrackerUIViewModel : ViewModelBase,
    IActivationAware, IDeactivationAware, IDisposable {
  private const string _keyPrefix = "Tracker_";

  private readonly Func<MAVLinkInterface> _activeComPort;
  private readonly Func<string[]> _serialPortNames;
  private readonly Func<string, int, ICommsSerial> _serialPortFactory;

  private IAntennaTrackerOutput? _tracker;
  private CancellationTokenSource? _loopCts;

  public ObservableCollection<string> Interfaces { get; } =
      new(AntennaTrackerOutputFactory.InterfaceNames);

  public ObservableCollection<string> Ports { get; } = new();

  public ObservableCollection<string> Bauds { get; } =
      new() { "4800", "9600", "14400", "19200", "28800", "38400", "57600", "115200" };

  [ObservableProperty]
  private string _selectedInterface = "Maestro";

  [ObservableProperty]
  private string _selectedPort = "";

  [ObservableProperty]
  private string _selectedBaud = "9600";

  [ObservableProperty]
  private string _connectText = "Connect";

  [ObservableProperty]
  private bool _controlsEnabled = true;

  [ObservableProperty]
  private string _status = "";

  [ObservableProperty]
  private bool _speedAccelEnabled = true;

  [ObservableProperty]
  private string _panRange = "360";

  [ObservableProperty]
  private string _panPwmRange = "1000";

  [ObservableProperty]
  private string _panCenter = "1500";

  [ObservableProperty]
  private string _panSpeed = "100";

  [ObservableProperty]
  private string _panAccel = "5";

  [ObservableProperty]
  private double _panTrim;

  [ObservableProperty]
  private double _panTrimMin = -180;

  [ObservableProperty]
  private double _panTrimMax = 180;

  [ObservableProperty]
  private bool _panReverse;

  [ObservableProperty]
  private string _tiltRange = "90";

  [ObservableProperty]
  private string _tiltPwmRange = "1000";

  [ObservableProperty]
  private string _tiltCenter = "1500";

  [ObservableProperty]
  private string _tiltSpeed = "100";

  [ObservableProperty]
  private string _tiltAccel = "5";

  [ObservableProperty]
  private double _tiltTrim;

  [ObservableProperty]
  private double _tiltTrimMin = -45;

  [ObservableProperty]
  private double _tiltTrimMax = 45;

  [ObservableProperty]
  private bool _tiltReverse;

  [ObservableProperty]
  private bool _manualMode;

  [ObservableProperty]
  private double _manualAzimuth;

  [ObservableProperty]
  private double _manualElevation;

  [ObservableProperty]
  private string _vehicleAzimuth = "--";

  [ObservableProperty]
  private string _vehicleElevation = "--";

  [ObservableProperty]
  private string _commandedAzimuth = "--";

  [ObservableProperty]
  private string _commandedElevation = "--";

  public bool IsRunning => _loopCts is { IsCancellationRequested: false };

  public AntennaTrackerUIViewModel()
      : this(
          () => AppState.comPort,
          SerialPort.GetPortNames,
          (port, baud) => new SerialPort { PortName = port, BaudRate = baud }) {
  }

  internal AntennaTrackerUIViewModel(
      Func<MAVLinkInterface> activeComPort,
      Func<string[]> serialPortNames,
      Func<string, int, ICommsSerial> serialPortFactory) {
    _activeComPort = activeComPort ?? throw new ArgumentNullException(nameof(activeComPort));
    _serialPortNames = serialPortNames ?? throw new ArgumentNullException(nameof(serialPortNames));
    _serialPortFactory =
        serialPortFactory ?? throw new ArgumentNullException(nameof(serialPortFactory));
    LoadSettings();
    if (!Interfaces.Contains(SelectedInterface)) {
      SelectedInterface = AntennaTrackerOutputFactory.Maestro;
    }
    RefreshPorts();
    UpdatePanTrimRange();
    UpdateTiltTrimRange();
    UpdateSpeedAccelEnabled();
  }

  public void Activate() {
    RefreshPorts();
    if (IsRunning) {
      ConnectText = "Disconnect";
    }
  }

  public void Deactivate() => SaveSettings();

  private void RefreshPorts() {
    Ports.Clear();
    foreach (var p in _serialPortNames().Distinct()) {
      Ports.Add(p);
    }

    if (!Ports.Contains(SelectedPort)) {
      SelectedPort = Ports.FirstOrDefault() ?? "";
    }
  }

  partial void OnSelectedInterfaceChanged(string value) => UpdateSpeedAccelEnabled();

  private void UpdateSpeedAccelEnabled() =>
      SpeedAccelEnabled = ControlsEnabled &&
          SelectedInterface == AntennaTrackerOutputFactory.Maestro;

  partial void OnPanRangeChanged(string value) => UpdatePanTrimRange();

  partial void OnTiltRangeChanged(string value) => UpdateTiltTrimRange();

  private void UpdatePanTrimRange() {
    PanTrimMin = -180;
    PanTrimMax = 180;
  }

  private void UpdateTiltTrimRange() {
    int range = ParseInt(TiltRange, 90);
    TiltTrimMin = range / 2 * -1;
    TiltTrimMax = range / 2;
  }

  partial void OnPanTrimChanged(double value) {
    if (_tracker != null) {
      _tracker.TrimPan = value;
    }
  }

  partial void OnTiltTrimChanged(double value) {
    if (_tracker != null) {
      _tracker.TrimTilt = value;
    }
  }

  partial void OnPanReverseChanged(bool value) {
    if (_tracker != null) {
      _tracker.PanReverse = value;
    }
  }

  partial void OnTiltReverseChanged(bool value) {
    if (_tracker != null) {
      _tracker.TiltReverse = value;
    }
  }

  partial void OnPanSpeedChanged(string value) {
    if (_tracker != null) {
      _tracker.PanSpeed = ParseInt(value, 0);
    }
  }

  partial void OnPanAccelChanged(string value) {
    if (_tracker != null) {
      _tracker.PanAccel = ParseInt(value, 0);
    }
  }

  partial void OnTiltSpeedChanged(string value) {
    if (_tracker != null) {
      _tracker.TiltSpeed = ParseInt(value, 0);
    }
  }

  partial void OnTiltAccelChanged(string value) {
    if (_tracker != null) {
      _tracker.TiltAccel = ParseInt(value, 0);
    }
  }

  [RelayCommand]
  private void Connect() {
    SaveSettings();

    if (IsRunning) {
      StopLoop();
      _tracker?.Dispose();
      _tracker = null;
      ControlsEnabled = true;
      UpdateSpeedAccelEnabled();
      ConnectText = "Connect";
      Status = "Disconnected.";
      return;
    }

    if (string.IsNullOrWhiteSpace(SelectedPort)) {
      Status = "No serial port selected.";
      return;
    }

    ICommsSerial serial;
    try {
      int baud = ParseRequiredInt(SelectedBaud, "baud rate", minimum: 1);
      serial = _serialPortFactory(SelectedPort, baud) ??
          throw new InvalidOperationException("Serial port factory returned no port.");
    } catch (Exception ex) {
      Status = "Error connecting: " + ex.Message;
      return;
    }

    IAntennaTrackerOutput driver;
    try {
      driver = AntennaTrackerOutputFactory.Create(SelectedInterface, serial);
    } catch (Exception ex) {
      serial.Dispose();
      Status = "Error selecting tracker interface: " + ex.Message;
      return;
    }

    try {
      int panRange = ParseRequiredInt(PanRange, "pan range", minimum: 1);
      driver.PanStartRange = panRange / 2 * -1;
      driver.PanEndRange = panRange / 2;
      driver.TrimPan = PanTrim;

      int tiltRange = ParseRequiredInt(TiltRange, "tilt range", minimum: 1);
      driver.TiltStartRange = tiltRange / 2 * -1;
      driver.TiltEndRange = tiltRange / 2;
      driver.TrimTilt = TiltTrim;

      driver.PanReverse = PanReverse;
      driver.TiltReverse = TiltReverse;

      driver.PanPWMRange = ParseRequiredInt(PanPwmRange, "pan PWM range", minimum: 1);
      driver.TiltPWMRange = ParseRequiredInt(TiltPwmRange, "tilt PWM range", minimum: 1);

      driver.PanPWMCenter = ParseRequiredInt(PanCenter, "pan PWM center", minimum: 1);
      driver.TiltPWMCenter = ParseRequiredInt(TiltCenter, "tilt PWM center", minimum: 1);

      driver.PanSpeed = ParseRequiredInt(PanSpeed, "pan speed", minimum: 0);
      driver.PanAccel = ParseRequiredInt(PanAccel, "pan acceleration", minimum: 0);
      driver.TiltSpeed = ParseRequiredInt(TiltSpeed, "tilt speed", minimum: 0);
      driver.TiltAccel = ParseRequiredInt(TiltAccel, "tilt acceleration", minimum: 0);
    } catch (Exception ex) {
      Status = "Invalid number entered: " + ex.Message;
      driver.Dispose();
      return;
    }

    if (!driver.Init(out var err)) {
      Status = err;
      driver.Dispose();
      return;
    }

    try {
      if (!driver.Setup()) {
        Status = "Tracker setup failed.";
        driver.Dispose();
        return;
      }
    } catch (Exception ex) {
      Status = "Tracker setup failed: " + ex.Message;
      driver.Dispose();
      return;
    }

    PanCenter = driver.PanPWMCenter.ToString(CultureInfo.InvariantCulture);
    TiltCenter = driver.TiltPWMCenter.ToString(CultureInfo.InvariantCulture);

    try {
      driver.PanAndTilt(0, 0);
    } catch (Exception ex) {
      Status = "Failed to set initial pan and tilt: " + ex.Message;
      driver.Dispose();
      return;
    }

    _tracker = driver;
    ControlsEnabled = false;
    UpdateSpeedAccelEnabled();
    ConnectText = "Disconnect";
    Status = "Connected (" + SelectedInterface + ").";
    StartLoop();
  }

  [RelayCommand]
  private void HomeCenter() {
    ManualAzimuth = 0;
    ManualElevation = 0;
    if (_tracker != null) {
      try {
        _tracker.PanAndTilt(0, 0);
      } catch (Exception ex) {
        Status = "Center failed: " + ex.Message;
      }
    }
  }

  [RelayCommand]
  private async Task FindTrimPan() {
    if (!IsRunning) {
      Status = "Connect to the tracker first.";
      return;
    }

    float snr = _activeComPort().MAV.cs.localsnrdb;
    if (snr == 0) {
      Status = "No valid SiK radio detected.";
      return;
    }

    Status = "Searching for best pan trim...";

    await Task.Run(() => {
      float pan = (float)PanTrim;
      float panRange = ParseInt(PanRange, 360);

      float ans = CheckPos(pan - panRange / 4, pan + panRange / 4 - 1, 30);
      ans = CheckPos(-30 + ans, 30 + ans, 5);
      ans = CheckPos(-5 + ans, 5 + ans, 1);

      SetPan(ans);
    });

    Status = "Pan trim search complete.";
  }

  private float CheckPos(float start, float end, float scale) {
    float lastsnr = 0;
    float best = 0;

    SetPan(start);
    Thread.Sleep(4000);

    for (float n = start; n < end; n += scale) {
      SetPan(n);
      Thread.Sleep(2000);

      float snr = _activeComPort().MAV.cs.localsnrdb;
      if (snr > lastsnr) {
        best = n;
        lastsnr = snr;
      }
    }

    return best;
  }

  private void SetPan(float angle) =>
      Dispatcher.UIThread.Post(() => PanTrim = angle);

  private void StartLoop() {
    _loopCts = new CancellationTokenSource();
    var token = _loopCts.Token;
    _ = Task.Run(() => {
      while (!token.IsCancellationRequested) {
        try {
          MAVLinkInterface comPort = _activeComPort();
          double vehicleAzimuth = comPort.MAV.cs.AZToMAV;
          double vehicleElevation = comPort.MAV.cs.ELToMAV;
          double az;
          double el;
          if (ManualMode) {
            az = ManualAzimuth;
            el = ManualElevation;
          } else {
            az = vehicleAzimuth;
            el = vehicleElevation;
          }

          _tracker?.PanAndTilt(az, el);

          Dispatcher.UIThread.Post(() => {
            VehicleAzimuth = vehicleAzimuth.ToString("0.0", CultureInfo.InvariantCulture);
            VehicleElevation = vehicleElevation.ToString("0.0", CultureInfo.InvariantCulture);
            CommandedAzimuth = az.ToString("0.0", CultureInfo.InvariantCulture);
            CommandedElevation = el.ToString("0.0", CultureInfo.InvariantCulture);
          });
        } catch {
        }

        Thread.Sleep(100);
      }
    }, token);
  }

  private void StopLoop() {
    var cts = _loopCts;
    _loopCts = null;
    if (cts == null) {
      return;
    }
    try {
      cts.Cancel();
    } catch (ObjectDisposedException) {
    }
    cts.Dispose();
  }

  private void LoadSettings() {
    SelectedInterface = Get("CMB_interface", SelectedInterface);
    SelectedPort = Get("CMB_serialport", SelectedPort);
    SelectedBaud = Get("CMB_baudrate", SelectedBaud);

    PanRange = Get("TXT_panrange", PanRange);
    PanPwmRange = Get("TXT_pwmrangepan", PanPwmRange);
    PanCenter = Get("TXT_centerpan", PanCenter);
    PanSpeed = Get("TXT_panspeed", PanSpeed);
    PanAccel = Get("TXT_panaccel", PanAccel);

    TiltRange = Get("TXT_tiltrange", TiltRange);
    TiltPwmRange = Get("TXT_pwmrangetilt", TiltPwmRange);
    TiltCenter = Get("TXT_centertilt", TiltCenter);
    TiltSpeed = Get("TXT_tiltspeed", TiltSpeed);
    TiltAccel = Get("TXT_tiltaccel", TiltAccel);

    PanTrim = Settings.Instance.GetInt32(_keyPrefix + "TRK_pantrim", 0);
    TiltTrim = Settings.Instance.GetInt32(_keyPrefix + "TRK_tilttrim", 0);
    PanReverse = Settings.Instance.GetBoolean(_keyPrefix + "CHK_revpan", false);
    TiltReverse = Settings.Instance.GetBoolean(_keyPrefix + "CHK_revtilt", false);
  }

  private void SaveSettings() {
    Set("CMB_interface", SelectedInterface);
    Set("CMB_serialport", SelectedPort);
    Set("CMB_baudrate", SelectedBaud);

    Set("TXT_panrange", PanRange);
    Set("TXT_pwmrangepan", PanPwmRange);
    Set("TXT_centerpan", PanCenter);
    Set("TXT_panspeed", PanSpeed);
    Set("TXT_panaccel", PanAccel);

    Set("TXT_tiltrange", TiltRange);
    Set("TXT_pwmrangetilt", TiltPwmRange);
    Set("TXT_centertilt", TiltCenter);
    Set("TXT_tiltspeed", TiltSpeed);
    Set("TXT_tiltaccel", TiltAccel);

    Set("TRK_pantrim", ((int)PanTrim).ToString(CultureInfo.InvariantCulture));
    Set("TRK_tilttrim", ((int)TiltTrim).ToString(CultureInfo.InvariantCulture));
    Set("CHK_revpan", PanReverse.ToString());
    Set("CHK_revtilt", TiltReverse.ToString());
  }

  private static string Get(string name, string fallback) {
    var key = _keyPrefix + name;
    return Settings.Instance.ContainsKey(key) && Settings.Instance[key] != null
        ? Settings.Instance[key]
        : fallback;
  }

  private static void Set(string name, string value) =>
      Settings.Instance[_keyPrefix + name] = value;

  private static int ParseInt(string value, int fallback) =>
      int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
                                                                                          : fallback;

  private static int ParseRequiredInt(string value, string field, int minimum) {
    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) {
      throw new FormatException(field + " must be an integer.");
    }
    if (parsed < minimum) {
      throw new ArgumentOutOfRangeException(field, parsed, field + " is below the safe minimum.");
    }
    return parsed;
  }

  public void Dispose() {
    SaveSettings();
    StopLoop();
    _tracker?.Dispose();
    _tracker = null;
  }
}
