using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.ViewModels;

public sealed record MavSystemChoice(byte SysId, byte CompId, string Label) {
  public override string ToString() => Label;
}

public partial class ConnectionViewModel : ViewModelBase, IDisposable {
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private readonly CancellationTokenSource _lifetimeCts = new();
  private readonly Task _lifetimeTask;

  internal event Action? Connected;

  public ObservableCollection<string> Ports { get; } = new();
  public ObservableCollection<int> Bauds { get; } =
      new() {
        1200, 2400, 4800, 9600, 19200, 38400, 57600, 111100, 115200, 230400,
        460800, 500000, 625000, 921600, 1000000, 1500000,
      };

  public ObservableCollection<MavSystemChoice> VehicleChoices { get; } = new();

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanEditBaud))]
  private string? _selectedPort;

  [ObservableProperty]
  private int _selectedBaud = 115200;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanEditConnection))]
  [NotifyPropertyChangedFor(nameof(CanEditBaud))]
  [NotifyPropertyChangedFor(nameof(VehicleSelectorVisible))]
  private bool _isConnected;

  [ObservableProperty]
  private MavSystemChoice? _selectedVehicle;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(VehicleSelectorVisible))]
  private bool _hasVehicleChoices;

  public bool CanEditConnection => !IsConnected;
  public bool CanEditBaud => !IsConnected && IsSerialEndpoint(SelectedPort);
  public bool VehicleSelectorVisible => IsConnected && HasVehicleChoices;

  [ObservableProperty]
  private string _connectText = "CONNECT";

  [ObservableProperty]
  private string _status = "";

  [ObservableProperty]
  private int _progress = -1;

  [ObservableProperty]
  private bool _readOnly;

  [ObservableProperty]
  private bool _autoConnect;

  partial void OnReadOnlyChanged(bool value) => _comPort.ReadOnly = value;

  partial void OnAutoConnectChanged(bool value) =>
      Settings.Instance["autoconnect"] = value.ToString();

  partial void OnSelectedPortChanged(string? value) {
    if (_initializing) {
      return;
    }
    SelectedBaud = SavedBaudForPort(value, SelectedBaud);
  }

  partial void OnSelectedBaudChanged(int value) {
    if (_initializing || value <= 0 || !IsSerialEndpoint(SelectedPort)) {
      return;
    }
    Settings.Instance[PortBaudKey(SelectedPort!)] = value.ToString();
  }

  partial void OnSelectedVehicleChanged(MavSystemChoice? value) {
    if (_updatingVehicleChoices || value == null || !IsConnected) {
      return;
    }

    _comPort.sysidcurrent = value.SysId;
    _comPort.compidcurrent = value.CompId;
    AppState.RaiseConnectionChanged();
    LoadSelectedVehicleParameters(value);
  }

  public ConnectionViewModel() {
    _initializing = true;
    _comPort.Progress += OnProgress;
    ApplyPersistentLinkSettings();
    RefreshPorts();
    string savedPort = Settings.Instance.ComPort;
    if (!string.IsNullOrWhiteSpace(savedPort)) {
      if (!Ports.Contains(savedPort)) {
        Ports.Insert(1, savedPort);
      }
      SelectedPort = savedPort;
    }
    if (int.TryParse(Settings.Instance.BaudRate, out var savedBaud) && savedBaud > 0) {
      SelectedBaud = savedBaud;
    } else {
      SelectedBaud = Settings.Instance.GetInt32("baudrate", SelectedBaud);
    }
    SelectedBaud = SavedBaudForPort(SelectedPort, SelectedBaud);
    _autoConnect = Settings.Instance.GetBoolean("autoconnect", false);
    _initializing = false;
    // Capture before scheduling: Dispose may otherwise win the race and accessing Token on a
    // disposed source from the delayed task body would throw.
    var lifetimeToken = _lifetimeCts.Token;
    _lifetimeTask = Task.Run(() => HeartbeatLoop(lifetimeToken));
  }

  private Services.ProgressReporter? _connectDialog;
  private readonly SemaphoreSlim _connectGate = new(1, 1);

  private readonly object _readerSync = new();
  private CancellationTokenSource? _readerCts;
  private int _readerGeneration;
  private DateTime _connectedAtUtc = DateTime.MinValue;
  private DateTime _lastVersionPollUtc = DateTime.MinValue;
  private bool _lastArmed;
  private int _homeRefreshRunning;
  private bool _initializing;
  private bool _updatingVehicleChoices;
  private string _vehicleChoiceSignature = "";
  private int _selectedVehicleParamLoadRunning;

  private int StartReader() {
    StopReader();

    foreach (var mav in _comPort.MAVlist) {
      mav.cs.rateattitude = CurrentState.rateattitudebackup;
      mav.cs.rateposition = CurrentState.ratepositionbackup;
      mav.cs.ratestatus = CurrentState.ratestatusbackup;
      mav.cs.ratesensors = CurrentState.ratesensorsbackup;
      mav.cs.raterc = CurrentState.ratercbackup;
    }
    RequestStreams();

    var cts = new CancellationTokenSource();
    // Capture the token now: a fast disconnect can cancel+dispose the CTS before the task
    // body ever runs, and cts.Token on a disposed source throws.
    var token = cts.Token;
    int generation;
    lock (_readerSync) {
      generation = ++_readerGeneration;
      _readerCts = cts;
    }
    _ = Task.Run(() => SerialReaderLoop(cts, token, generation));
    return generation;
  }

  private void RequestStreams() {
    try {
      foreach (var mav in _comPort.MAVlist) {
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTENDED_STATUS, mav.cs.ratestatus, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION, mav.cs.rateposition, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA1, mav.cs.rateattitude, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA2, mav.cs.rateattitude, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA3, mav.cs.ratesensors, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.RAW_SENSORS, mav.cs.ratesensors, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.RC_CHANNELS, mav.cs.raterc, mav.sysid, mav.compid);
      }
    } catch {

    }
  }

  private void StopReader() {
    CancellationTokenSource? cts;
    lock (_readerSync) {
      _readerGeneration++;
      cts = _readerCts;
      _readerCts = null;
    }
    if (cts == null) {
      return;
    }
    try {
      cts.Cancel();
    } catch (ObjectDisposedException) {
    }
    cts.Dispose();
  }

  public void Shutdown() {
    StopReader();
    try {
      if (_comPort.BaseStream?.IsOpen == true) {
        _comPort.Close();
      }
    } catch {

    }
    CloseLogs();
  }

  public void Dispose() {
    _comPort.Progress -= OnProgress;
    _lifetimeCts.Cancel();
    Shutdown();
    try {
      _lifetimeTask.Wait(TimeSpan.FromSeconds(1));
    } catch {
    }
    _lifetimeCts.Dispose();
  }

  private async Task SerialReaderLoop(CancellationTokenSource self, CancellationToken ct,
      int generation) {
    int consecutiveErrors = 0;
    while (!ct.IsCancellationRequested) {

      if (_comPort.BaseStream?.IsOpen != true) {
        HandleLinkLost(self, generation);
        break;
      }

      try {

        if (_comPort.giveComport == false) {
          var now = DateTime.UtcNow;
          var newestPacket = NewestPacketUtc();
          if (ConnectionHealth.IsSilent(
                  now, newestPacket, _connectedAtUtc, TimeSpan.FromSeconds(10))) {
            SetLinkQualityLost();
            if (ConnectionHealth.ShouldCloseSilentLink(
                    _comPort.MAV.cs.armed, now, newestPacket, _connectedAtUtc,
                    TimeSpan.FromSeconds(10))) {
              HandleLinkLost(self, generation);
              break;
            }
            // Keep an armed radio link open through a telemetry fade so it can recover, matching
            // upstream. Disarmed dead links are closed above to release joystick/output resources.
          }
        }

        if (_comPort.giveComport == false) {
          var start = DateTime.UtcNow;
          while (_comPort.giveComport == false && _comPort.BaseStream?.IsOpen == true &&
                 _comPort.BaseStream.BytesToRead > 10 && !ct.IsCancellationRequested &&
                 start.AddSeconds(1) > DateTime.UtcNow) {
            await _comPort.readPacketAsync().ConfigureAwait(false);
          }

          foreach (var mav in _comPort.MAVlist) {
            mav.cs.UpdateCurrentSettings(null, false, _comPort, mav);
          }
          RefreshHomeOnArmTransition(ct);
        }

        consecutiveErrors = 0;
        await Task.Delay(_comPort.giveComport ? 50 : 1, ct).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      } catch {

        if (++consecutiveErrors >= 5) {
          HandleLinkLost(self, generation);
          break;
        }
        try {
          await Task.Delay(50, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          break;
        }
      }
    }
  }

  private void SendHeartbeat() {
    try {
      var mav = _comPort.MAV;
      _comPort.sendPacket(
          new MAVLink.mavlink_heartbeat_t {
            type = (byte)MAVLink.MAV_TYPE.GCS,
            autopilot = (byte)MAVLink.MAV_AUTOPILOT.INVALID,
            mavlink_version = 3,
          },
          mav.sysid, mav.compid);
    } catch {
    }
  }

  private async Task HeartbeatLoop(CancellationToken ct) {
    while (!ct.IsCancellationRequested) {
      try {
        if (_comPort.BaseStream?.IsOpen == true) {
          if (Settings.Instance.GetBoolean("CHK_GCSheartbeat", true)) {
            SendHeartbeat();
          }
          if (_connectedAtUtc != DateTime.MinValue && !_comPort.giveComport &&
              !_comPort.MAV.cs.armed &&
              DateTime.UtcNow > _connectedAtUtc.AddSeconds(60)) {
            _comPort.getParamPoll();
            _comPort.getParamPoll();
          }
          if (_connectedAtUtc != DateTime.MinValue && !_comPort.giveComport &&
              DateTime.UtcNow > _lastVersionPollUtc.AddSeconds(20)) {
            _lastVersionPollUtc = DateTime.UtcNow;
            foreach (var mav in _comPort.MAVlist) {
              if (mav.cs.capabilities == 0 && mav.cs.version < new Version(0, 1)) {
                _comPort.getVersion(mav.sysid, mav.compid, false);
              }
            }
          }
        }
        RefreshVehicleChoices();
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        return;
      } catch {
        try {
          await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          return;
        }
      }
    }
  }

  private void RefreshVehicleChoices() {
    var choices = _comPort.BaseStream?.IsOpen == true
        ? _comPort.MAVlist.ToArray()
            .Where(mav => mav.sysid != 0 &&
                mav.compid != (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_MISSIONPLANNER)
            .Select(mav => new MavSystemChoice(
                mav.sysid, mav.compid, VehicleChoiceLabel(mav)))
            .OrderBy(choice => choice.SysId)
            .ThenBy(choice => choice.CompId)
            .ToArray()
        : Array.Empty<MavSystemChoice>();
    string signature = string.Join(";", choices.Select(choice =>
        $"{choice.SysId}:{choice.CompId}:{choice.Label}"));
    if (string.Equals(signature, _vehicleChoiceSignature, StringComparison.Ordinal)) {
      return;
    }
    _vehicleChoiceSignature = signature;

    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
      _updatingVehicleChoices = true;
      try {
        VehicleChoices.Clear();
        foreach (var choice in choices) {
          VehicleChoices.Add(choice);
        }
        HasVehicleChoices = choices.Length > 0;
        SelectedVehicle = choices.FirstOrDefault(choice =>
            choice.SysId == _comPort.sysidcurrent && choice.CompId == _comPort.compidcurrent)
            ?? choices.FirstOrDefault();
      } finally {
        _updatingVehicleChoices = false;
      }
    });
  }

  private static string VehicleChoiceLabel(MAVState mav) {
    string component;
    if (mav.CANNode) {
      component = string.IsNullOrWhiteSpace(mav.VersionString)
          ? "CAN node"
          : mav.VersionString;
    } else if (mav.compid == (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1) {
      component = mav.aptype.ToString();
    } else {
      component = ((MAVLink.MAV_COMPONENT)mav.compid).ToString();
      const string prefix = "MAV_COMP_ID_";
      if (component.StartsWith(prefix, StringComparison.Ordinal)) {
        component = component.Substring(prefix.Length);
      }
      component = component.Replace('_', ' ');
    }
    return $"{mav.sysid}:{mav.compid} {component}";
  }

  private void LoadSelectedVehicleParameters(MavSystemChoice choice) {
    var mav = _comPort.MAVlist[choice.SysId, choice.CompId];
    if (mav.param.TotalReceived >= mav.param.TotalReported ||
        Interlocked.Exchange(ref _selectedVehicleParamLoadRunning, 1) != 0) {
      return;
    }

    Status = $"Loading parameters for {choice.Label}…";
    _ = Task.Run(() => {
      Exception? error = null;
      try {
        _comPort.getParamListMavftp(choice.SysId, choice.CompId);
      } catch (Exception ex) {
        error = ex;
      } finally {
        Interlocked.Exchange(ref _selectedVehicleParamLoadRunning, 0);
      }

      Avalonia.Threading.Dispatcher.UIThread.Post(() => {
        if (_comPort.BaseStream?.IsOpen != true ||
            _comPort.sysidcurrent != choice.SysId || _comPort.compidcurrent != choice.CompId) {
          return;
        }
        Status = error == null
            ? $"Selected {choice.Label}. {mav.param.Count} params."
            : $"Selected {choice.Label}; parameter load failed: {error.Message}";
      });
    });
  }

  private DateTime NewestPacketUtc() {
    DateTime newest = DateTime.MinValue;
    foreach (var mav in _comPort.MAVlist) {
      if (mav.lastvalidpacket > newest) {
        newest = mav.lastvalidpacket;
      }
    }
    return newest;
  }

  private void SetLinkQualityLost() {
    foreach (var mav in _comPort.MAVlist) {
      mav.cs.linkqualitygcs = 0;
    }
  }

  internal void PrepareForConnection() {
    ApplyPersistentLinkSettings();
    _lastArmed = false;
    _lastVersionPollUtc = DateTime.MinValue;
    foreach (var mav in _comPort.MAVlist) {
      mav.cs.ResetInternals();
    }
  }

  internal void AdoptOpenConnection(string endpoint) {
    if (_comPort.BaseStream?.IsOpen != true) {
      return;
    }
    OpenLogs();
    IsConnected = true;
    ConnectText = "DISCONNECT";
    Status = $"Connected to {endpoint}. {_comPort.MAV.param.Count} params.";
    _connectedAtUtc = DateTime.UtcNow;
    StartReader();
    RefreshVehicleChoices();
    RawParamsViewModel.SaveSnapshot(_comPort);
    AppState.RaiseConnectionChanged();
    RaiseConnected();
  }

  private void RaiseConnected() {
    try {
      Connected?.Invoke();
    } catch {
      // A UI convenience such as auto-loading a mission must never tear down a valid link.
    }
  }

  private void StartBackgroundParameterLoad(int generation) {
    byte sysid = _comPort.MAV.sysid;
    byte compid = _comPort.MAV.compid;
    _ = Task.Run(() => {
      Exception? error = null;
      try {
        // This upstream API uses MAVFTP when the vehicle advertises it and transparently falls
        // back to the parameter protocol otherwise.
        _comPort.getParamListMavftp(sysid, compid);
        RawParamsViewModel.SaveSnapshot(_comPort);
      } catch (Exception ex) {
        error = ex;
      }

      Avalonia.Threading.Dispatcher.UIThread.Post(() => {
        lock (_readerSync) {
          if (_readerGeneration != generation || _readerCts == null ||
              _comPort.BaseStream?.IsOpen != true) {
            return;
          }
        }
        Status = error == null
            ? $"Connected. {_comPort.MAV.param.Count} params loaded in background."
            : "Connected, but background parameter load failed: " + error.Message;
      });
    });
  }

  private bool TryLoadFreshParameterCache(out int count) {
    count = 0;
    if (!Settings.Instance.GetBoolean("UseCachedParams", false)) {
      return false;
    }

    try {
      string path = _comPort.MAV.ParamCachePath;
      if (!ParameterCachePolicy.IsFresh(
              path, DateTime.Now, TimeSpan.FromHours(1), File.GetLastWriteTime)) {
        return false;
      }

      var cached = File.ReadAllText(path).FromJSON<MAVLink.MAVLinkParamList>();
      if (cached == null || cached.Count == 0) {
        return false;
      }
      foreach (var parameter in cached) {
        _comPort.MAV.param.Add(parameter);
      }
      _comPort.MAV.param.TotalReported = _comPort.MAV.param.TotalReceived;
      count = _comPort.MAV.param.Count;
      return count > 0;
    } catch {
      // A corrupt, stale or concurrently-written cache is never fatal; fall back to the live
      // MAVFTP/parameter protocol path below.
      return false;
    }
  }

  private void RefreshHomeOnArmTransition(CancellationToken ct) {
    bool armed = _comPort.MAV.cs.armed;
    bool becameArmed = armed && !_lastArmed;
    _lastArmed = armed;
    if (!becameArmed || _comPort.MAV.apname == MAVLink.MAV_AUTOPILOT.INVALID ||
        _comPort.MAV.aptype == MAVLink.MAV_TYPE.GIMBAL ||
        Interlocked.Exchange(ref _homeRefreshRunning, 1) != 0) {
      return;
    }

    _ = Task.Run(async () => {
      try {
        while (_comPort.giveComport && _comPort.BaseStream?.IsOpen == true) {
          await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (!ct.IsCancellationRequested && _comPort.BaseStream?.IsOpen == true) {
          _comPort.MAV.cs.HomeLocation = new PointLatLngAlt(
              _comPort.getWP(_comPort.MAV.sysid, _comPort.MAV.compid, 0));
        }
      } catch (OperationCanceledException) {
      } catch {
        // Home refresh is best-effort, matching upstream's arm-transition helper.
      } finally {
        Interlocked.Exchange(ref _homeRefreshRunning, 0);
      }
    });
  }

  private static void ApplyPersistentLinkSettings() {
    var settings = Settings.Instance;
    CurrentState.rateattitudebackup = Math.Max(0, settings.GetInt32("CMB_rateattitude", 4));
    CurrentState.ratepositionbackup = Math.Max(0, settings.GetInt32("CMB_rateposition", 2));
    CurrentState.ratestatusbackup = Math.Max(0, settings.GetInt32("CMB_ratestatus", 2));
    CurrentState.ratercbackup = Math.Max(0, settings.GetInt32("CMB_raterc", 2));
    CurrentState.ratesensorsbackup = Math.Max(0, settings.GetInt32("CMB_ratesensors", 2));
    int sysid = settings.ContainsKey("gcsid")
        ? settings.GetInt32("gcsid", 255)
        : settings.GetInt32("GCS_sysid", 255);
    MAVLinkInterface.gcssysid = (byte)Math.Clamp(sysid, 1, 255);
  }

  private void HandleLinkLost(CancellationTokenSource self, int generation) {
    lock (_readerSync) {
      if (_readerCts != self || _readerGeneration != generation) {
        // StopReader or a newer reader already owns the shared MAVLink interface. In particular,
        // an old reader must never close a newly-opened connection.
        return;
      }

      _readerCts = null;
      _connectedAtUtc = DateTime.MinValue;
      try {
        _comPort.Close();
      } catch {
      }
      CloseLogs();
    }
    self.Dispose();

    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
      lock (_readerSync) {
        // A reconnect or an explicit disconnect may have happened while this notification was
        // queued on the UI thread.
        if (_readerGeneration != generation || _readerCts != null) {
          return;
        }
      }
      IsConnected = false;
      ConnectText = "CONNECT";
      Status = "Connection lost.";
      RefreshVehicleChoices();
      AppState.RaiseConnectionChanged();
    });
  }

  private void OnProgress(int percent, string status) =>
      Avalonia.Threading.Dispatcher.UIThread.Post(() => {
        Progress = percent;
        if (!string.IsNullOrEmpty(status)) {
          Status = status;
        }
        _connectDialog?.Set(percent < 0 ? 0 : percent, status);
      });

  [RelayCommand]
  private void RefreshPorts() {
    var cur = SelectedPort;
    Ports.Clear();
    Ports.Add("AUTO");
    foreach (var p in DedupePorts(SerialPort.GetPortNames())) {
      Ports.Add(p);
    }
    foreach (var net in new[] { "TCP", "UDP", "UDPCl", "WS" }) {
      Ports.Add(net);
    }

    // Preserve a configured removable serial device even while it is unplugged. It can then be
    // selected automatically when the app is launched after the device appears again.
    if (!string.IsNullOrWhiteSpace(cur) && !Ports.Contains(cur)) {
      Ports.Insert(1, cur);
    }

    SelectedPort = Ports.Contains(cur ?? "") ? cur : Ports.FirstOrDefault(p => p != "AUTO");
  }

  private static readonly string[] _internalPorts = { "Bluetooth-Incoming-Port", "debug-console" };

  private static IEnumerable<string> DedupePorts(string[] names) {
    var all = names.Distinct()
        .Where(n => !_internalPorts.Any(p => n.Contains(p, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    var cuDevices = new HashSet<string>(
        all.Where(n => n.Contains("/cu.")).Select(n => n.Replace("/cu.", "/tty.")));
    return all.Where(n => !cuDevices.Contains(n)).OrderBy(n => n);
  }

  internal static bool IsSerialEndpoint(string? port) =>
      !string.IsNullOrWhiteSpace(port) && port is not "AUTO" and not "TCP" and not "UDP"
          and not "UDPCl" and not "WS";

  internal static string PortBaudKey(string port) => port.Replace(" ", "_") + "_BAUD";

  private static int SavedBaudForPort(string? port, int fallback) {
    if (!IsSerialEndpoint(port)) {
      return fallback;
    }
    int saved = Settings.Instance.GetInt32(PortBaudKey(port!), fallback);
    return saved > 0 ? saved : fallback;
  }

  private void OpenLogs() {
    try {
      Directory.CreateDirectory(Settings.Instance.LogDir);
      var dt = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
      string tlog = Settings.Instance.LogDir + Path.DirectorySeparatorChar + dt + ".tlog";
      string rlog = Settings.Instance.LogDir + Path.DirectorySeparatorChar + dt + ".rlog";
      int a = 1;
      while (File.Exists(tlog) || File.Exists(rlog)) {
        dt = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + "-" + a++;
        tlog = Settings.Instance.LogDir + Path.DirectorySeparatorChar + dt + ".tlog";
        rlog = Settings.Instance.LogDir + Path.DirectorySeparatorChar + dt + ".rlog";
      }
      _comPort.logfile =
          new BufferedStream(File.Open(tlog, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
      _comPort.rawlogfile =
          new BufferedStream(File.Open(rlog, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
    } catch (Exception ex) {
      CloseLogs();
      Status = "Telemetry logging disabled: " + ex.Message;
    }
  }

  private void CloseLogs() {
    try {
      _comPort.logfile?.Close();
      _comPort.rawlogfile?.Close();
    } catch {

    }
    _comPort.logfile = null;
    _comPort.rawlogfile = null;
  }

  [RelayCommand]
  private async Task ToggleConnect() {
    if (_comPort.BaseStream?.IsOpen == true) {
      double displayedSpeed = _comPort.MAV.cs.groundspeed * CurrentState.multiplierspeed;
      if (_comPort.MAV.cs.groundspeed > 4 && !await Services.Dialogs.Confirm(
              "Disconnect",
              $"The vehicle is still moving at {displayedSpeed:0.0} {CurrentState.SpeedUnit}. Disconnect anyway?")) {
        return;
      }
      await DisconnectAsync("Disconnected.");
      return;
    }

    await ConnectAsync(interactive: true);
  }

  internal async Task TryAutoConnectAsync() {
    if (!AutoConnect || _comPort.BaseStream?.IsOpen == true ||
        string.IsNullOrWhiteSpace(SelectedPort)) {
      return;
    }

    Status = $"Auto-connecting to {SelectedPort}…";
    await ConnectAsync(interactive: false);
  }

  internal async Task<(bool Connected, string Error)> ConnectPreparedStreamAsync(
      ICommsSerial stream, string endpoint, bool getParams) {
    if (!await _connectGate.WaitAsync(0)) {
      return (false, "Another connection attempt is already running.");
    }

    try {
      if (_comPort.BaseStream?.IsOpen == true) {
        return (false, "Another vehicle connection is already active.");
      }
      _comPort.BaseStream = stream;
      PrepareForConnection();
      await Task.Run(() =>
          _comPort.Open(getparams: getParams, skipconnectedcheck: true, showui: false));
      if (_comPort.BaseStream?.IsOpen != true) {
        return (false, $"Could not connect to {endpoint}.");
      }
      AdoptOpenConnection(endpoint);
      return (true, "");
    } catch (Exception ex) {
      try {
        _comPort.Close();
      } catch {
      }
      CloseLogs();
      return (false, ex.Message);
    } finally {
      _connectGate.Release();
    }
  }

  internal async Task DisconnectAsync(string status) {
    StopReader();
    _connectedAtUtc = DateTime.MinValue;
    await Task.Run(() => {
      try {
        _comPort.Close();
      } catch {
      }
    });
    CloseLogs();
    IsConnected = false;
    ConnectText = "CONNECT";
    Status = status;
    RefreshVehicleChoices();
    AppState.RaiseConnectionChanged();
  }

  private async Task ConnectAsync(bool interactive) {
    if (!await _connectGate.WaitAsync(0)) {
      Status = "A connection attempt is already running.";
      return;
    }

    var sel = SelectedPort;
    if (string.IsNullOrEmpty(sel)) {
      if (interactive) {
        await Services.Dialogs.Alert("Connect", "No port selected.");
      } else {
        Status = "Auto-connect skipped: no saved port.";
      }
      _connectGate.Release();
      return;
    }

    try {
      ICommsSerial? stream = await BuildStreamAsync(sel, interactive);
      if (stream == null) {
        Status = "";
        return;
      }

      _comPort.BaseStream = stream;

      PrepareForConnection();

      if (stream is SerialPort serial &&
          Settings.Instance.GetBoolean("CHK_resetapmonconnect", false)) {
        try {
          serial.DtrEnable = false;
          serial.RtsEnable = false;
          serial.toggleDTR();
        } catch {
          // Some serial implementations cannot toggle before opening; normal Open still proceeds.
        }
      }

      OpenLogs();

      var dlg = new Services.ProgressReporter("Connecting Mavlink");
      _connectDialog = dlg;

      AppState.ActiveConnectReporter = dlg;
      dlg.Set(0, $"Connecting {sel}…");

      dlg.Token.Register(() => {
        _ = Task.Run(() => {
          try {
            _comPort.Close();
          } catch {

          }
        });
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
          dlg.Close();
          CloseLogs();
          IsConnected = false;
          ConnectText = "CONNECT";
          Status = "";
          AppState.RaiseConnectionChanged();
        });
      });
      dlg.Show2();

      Exception? openError = null;
      bool backgroundParamLoad = false;
      bool cachedParamsLoaded = false;
      int cachedParamCount = 0;
      try {

        await Task.Run(() => _comPort.Open(getparams: false, skipconnectedcheck: true, showui: true));
        if (_comPort.BaseStream.IsOpen && !dlg.CancelRequested &&
            _comPort.MAV.compid != (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_PERIPHERAL) {
          cachedParamsLoaded = TryLoadFreshParameterCache(out cachedParamCount);
          if (!cachedParamsLoaded) {
            backgroundParamLoad = Settings.Instance.GetBoolean("Params_BG", false);
            if (!backgroundParamLoad) {
              await Task.Run(() => _comPort.getParamList());
            }
          }
        }
      } catch (Exception ex) {
        openError = ex;
      }
      _connectDialog = null;
      AppState.ActiveConnectReporter = null;

      if (dlg.CancelRequested) {
        dlg.Close();
        try {
          _comPort.Close();
        } catch {

        }
        CloseLogs();
        IsConnected = false;
        ConnectText = "CONNECT";
        Status = "";
        AppState.RaiseConnectionChanged();
        return;
      }

      if (openError != null) {
        dlg.Close();
        try {
          _comPort.Close();
        } catch {
        }
        CloseLogs();
        IsConnected = false;
        Status = interactive ? "" : "Auto-connect failed: " + openError.Message;
        if (interactive) {
          await Services.Dialogs.Alert("Connection error", openError.Message);
        }
        return;
      }

      IsConnected = _comPort.BaseStream.IsOpen;
      ConnectText = IsConnected ? "DISCONNECT" : "CONNECT";
      if (IsConnected) {
        Settings.Instance.ComPort = sel;
        Settings.Instance.BaudRate = SelectedBaud.ToString();
        if (IsSerialEndpoint(sel)) {
          Settings.Instance[PortBaudKey(sel)] = SelectedBaud.ToString();
        }
        Status = cachedParamsLoaded
            ? $"Connected. Reused {cachedParamCount} cached params."
            : backgroundParamLoad
                ? "Connected. Loading parameters in background…"
                : $"Connected. {_comPort.MAV.param.Count} params.";
        _connectedAtUtc = DateTime.UtcNow;
        int generation = StartReader();
        RefreshVehicleChoices();

        if (backgroundParamLoad) {
          StartBackgroundParameterLoad(generation);
        } else {
          RawParamsViewModel.SaveSnapshot(_comPort);
        }
        RaiseConnected();

        dlg.Set(100, Status);
        await Task.Delay(1200);
        dlg.Close();
      } else {
        dlg.Close();
        CloseLogs();
        Status = interactive ? "" : $"Auto-connect failed on {sel}.";
        if (interactive) {
          await Services.Dialogs.Alert("Connection failed", $"Could not connect on {sel}.");
        }
      }
      AppState.RaiseConnectionChanged();
    } catch (Exception ex) {
      AppState.ActiveConnectReporter = null;
      try {
        _comPort.Close();
      } catch {
      }
      CloseLogs();
      Status = interactive ? "" : "Auto-connect failed: " + ex.Message;
      IsConnected = false;
      ConnectText = "CONNECT";
      if (interactive) {
        await Services.Dialogs.Alert("Connection error", ex.Message);
      }
    } finally {
      _connectGate.Release();
    }
  }

  private async Task<ICommsSerial?> BuildStreamAsync(string sel, bool interactive) {
    switch (sel) {
      case "AUTO":
        return await Task.Run(ScanForPort);

      case "TCP": {
          var defaults = new[] { Setting("TCP_host", "127.0.0.1"), Setting("TCP_port", "5760") };
          var v = interactive
              ? await PromptAsync("TCP client", "Host / IP", defaults[0], "Remote port", defaults[1])
              : defaults;
          if (v == null) {
            return null;
          }

          Store("TCP_host", v[0]);
          Store("TCP_port", v[1]);
          return new TcpSerial();
        }

      case "UDPCl": {
          var defaults = new[] { Setting("UDP_host", "127.0.0.1"), Setting("UDP_port", "14550") };
          var v = interactive
              ? await PromptAsync("UDP client", "Host / IP", defaults[0], "Remote port", defaults[1])
              : defaults;
          if (v == null) {
            return null;
          }

          Store("UDP_host", v[0]);
          Store("UDP_port", v[1]);
          return new UdpSerialConnect();
        }

      case "UDP": {
          var defaults = new[] { Setting("UDP_port", "14550"), "" };
          var v = interactive
              ? await PromptAsync("UDP listener", "Local port", defaults[0], null, "")
              : defaults;
          if (v == null) {
            return null;
          }

          Store("UDP_port", v[0]);
          return new UdpSerial();
        }

      case "WS": {
          var defaults = new[] { Setting("WS_url", "ws://127.0.0.1:8080"), "" };
          var v = interactive
              ? await PromptAsync("WebSocket", "URL", defaults[0], null, "")
              : defaults;
          if (v == null) {
            return null;
          }

          Store("WS_url", v[0]);
          return new WebSocket();
        }

      default:
        return new SerialPort {
          PortName = sel,
          BaudRate = SelectedBaud,
          espFix = Settings.Instance.GetBoolean("CHK_rtsresetesp32", false),
        };
    }
  }

  private ICommsSerial? ScanForPort() {
    CommsSerialScan.Scan(false);
    var deadline = DateTime.Now.AddSeconds(20);
    while (!CommsSerialScan.foundport && CommsSerialScan.run == 1 && DateTime.Now < deadline) {
      Thread.Sleep(300);
    }
    return CommsSerialScan.portinterface?.FirstOrDefault();
  }

  private static string Setting(string key, string fallback) {
    var v = AppState.CommsSettings.TryGetValue(key, out var s) ? s : "";
    if (string.IsNullOrEmpty(v)) {
      v = Settings.Instance["connection_" + key] ?? "";
    }
    return string.IsNullOrEmpty(v) ? fallback : v;
  }

  private static void Store(string key, string? value) {
    AppState.CommsSettings[key] = value ?? "";
    Settings.Instance["connection_" + key] = value ?? "";
  }

  private static async Task<string[]?> PromptAsync(
      string title, string l1, string v1, string? l2, string v2) {
    var owner = (Avalonia.Application.Current?.ApplicationLifetime
                 as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (owner == null) {
      return new[] { v1, v2 };
    }

    var r = await ConnectDialog.Show(owner, title, l1, v1, l2, v2);
    if (r == null) {
      return null;
    }

    return new[] { r[0] ?? "", r[1] ?? "" };
  }
}

internal static class ConnectionHealth {
  internal static bool ShouldCloseSilentLink(bool armed, DateTime nowUtc,
      DateTime newestPacketUtc, DateTime connectedAtUtc, TimeSpan timeout) =>
      !armed && IsSilent(nowUtc, newestPacketUtc, connectedAtUtc, timeout);

  internal static bool IsSilent(DateTime nowUtc, DateTime newestPacketUtc,
      DateTime connectedAtUtc, TimeSpan timeout) {
    if (connectedAtUtc == DateTime.MinValue) {
      return false;
    }
    // MAVState can retain the timestamp from the previous endpoint. A new session gets its full
    // grace period, but is still closed if it never receives a first packet.
    DateTime baseline = newestPacketUtc > connectedAtUtc ? newestPacketUtc : connectedAtUtc;
    return nowUtc - baseline > timeout;
  }
}

internal static class ParameterCachePolicy {
  internal static bool IsFresh(
      string? path,
      DateTime now,
      TimeSpan maximumAge,
      Func<string, DateTime>? getLastWriteTime = null,
      Func<string, bool>? fileExists = null) {
    if (string.IsNullOrWhiteSpace(path) || maximumAge < TimeSpan.Zero) {
      return false;
    }
    fileExists ??= File.Exists;
    getLastWriteTime ??= File.GetLastWriteTime;
    return fileExists(path) && getLastWriteTime(path) > now.Subtract(maximumAge);
  }
}
