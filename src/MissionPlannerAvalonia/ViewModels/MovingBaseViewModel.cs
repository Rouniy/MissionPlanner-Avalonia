using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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

public partial class MovingBaseViewModel : ViewModelBase, IDisposable {
  internal const string TcpHost = "TCP Host";
  internal const string TcpClient = "TCP Client";
  internal const string UdpHost = "UDP Host";
  internal const string UdpClient = "UDP Client";

  private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
  private readonly Func<bool, NmeaVehicleTarget?> _activeTarget;
  private readonly Action<NmeaVehicleTarget, PointLatLngAlt> _setBase;
  private readonly Action<NmeaVehicleTarget, PointLatLngAlt> _updateRally;
  private readonly Func<NmeaVehicleTarget, Task<bool>> _confirmRally;
  private readonly bool _subscribedToAppState;
  private CancellationTokenSource? _cts;
  private ICommsSerial? _input;
  private TcpListener? _listener;
  private Task? _readerTask;
  private Task? _acceptTask;
  private NmeaVehicleTarget? _boundTarget;
  private volatile bool _targetInvalidated;
  private int _stopScheduled;
  private bool _disposed;

  public MovingBaseViewModel()
      : this(
          NmeaVehicleSession.CaptureActive,
          static (target, location) => target.Link.MAV.cs.Base = location,
          UpdateVehicleRallyPoint,
          static target => Dialogs.ConfirmDangerous(
              "Enable Moving Base Rally Updates",
              $"Moving Base will overwrite Rally Point 0 on {NmeaVehicleSession.Describe(target)} "
              + "every five seconds using the incoming GPS position. Verify the selected modem, "
              + "rally altitude and recovery plan before continuing.",
              "Enable Rally Updates"),
          subscribeToAppState: true) {
  }

  internal MovingBaseViewModel(
      Func<bool, NmeaVehicleTarget?> activeTarget,
      Action<NmeaVehicleTarget, PointLatLngAlt> setBase,
      Action<NmeaVehicleTarget, PointLatLngAlt> updateRally,
      Func<NmeaVehicleTarget, Task<bool>> confirmRally,
      bool subscribeToAppState = false) {
    _activeTarget = activeTarget;
    _setBase = setBase;
    _updateRally = updateRally;
    _confirmRally = confirmRally;
    _subscribedToAppState = subscribeToAppState;
    RefreshInputs();
    var settings = Settings.Instance;
    SelectedBaud = LoadInt(settings, "MovingBaseBaud", 4800);
    NetworkHost = settings["MovingBaseHost"] ?? "127.0.0.1";
    NetworkPort = LoadInt(settings, "MovingBasePort", 14551);
    UpdateRateHz = LoadDouble(settings, "MovingBaseRate", 0.5);
    UpdateRallyPoint = settings.GetBoolean("MovingBaseUpdateRally", false);
    ShowRelativeAltitude = settings.GetBoolean("MovingBaseRelativeAlt", false);
    string? savedInput = settings["MovingBaseInput"];
    if (!string.IsNullOrWhiteSpace(savedInput) && Inputs.Contains(savedInput)) {
      SelectedInput = savedInput;
    }
    RefreshTargetDescription();
    if (_subscribedToAppState) {
      AppState.ConnectionChanged += OnConnectionChanged;
    }
  }

  public ObservableCollection<string> Inputs { get; } = new();

  public ObservableCollection<int> Bauds { get; } = new() {
      4800, 9600, 14400, 19200, 28800, 38400, 57600, 115200,
  };

  public ObservableCollection<double> Rates { get; } = new() { 0.25, 0.5, 1, 2 };

  [ObservableProperty]
  private string? _selectedInput;

  [ObservableProperty]
  private int _selectedBaud;

  [ObservableProperty]
  private string _networkHost = "127.0.0.1";

  [ObservableProperty]
  private int _networkPort = 14551;

  [ObservableProperty]
  private double _updateRateHz = 0.5;

  [ObservableProperty]
  private bool _updateRallyPoint;

  [ObservableProperty]
  private bool _showRelativeAltitude;

  [ObservableProperty]
  private string _status = "Stopped.";

  [ObservableProperty]
  private string _connectButtonText = "Connect";

  [ObservableProperty]
  private string _location = "No moving-base fix received.";

  [ObservableProperty]
  private string _targetDescription = "No vehicle selected.";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanEditSettings))]
  private bool _busy;

  public bool IsRunning => _cts != null;
  public bool CanEditSettings => !Busy && !IsRunning;
  public bool IsSerialInput => !IsNetworkInput(SelectedInput);
  public bool IsNetworkClient => SelectedInput is TcpClient or UdpClient;
  public string NetworkPortLabel => SelectedInput is TcpHost or UdpHost ? "Local port" : "Remote port";

  partial void OnSelectedInputChanged(string? value) {
    OnPropertyChanged(nameof(IsSerialInput));
    OnPropertyChanged(nameof(IsNetworkClient));
    OnPropertyChanged(nameof(NetworkPortLabel));
  }

  [RelayCommand]
  private void RefreshInputs() {
    string? selected = SelectedInput;
    Inputs.Clear();
    foreach (string port in SerialPort.GetPortNames().Distinct().OrderBy(item => item)) {
      Inputs.Add(port);
    }
    Inputs.Add(TcpHost);
    Inputs.Add(TcpClient);
    Inputs.Add(UdpHost);
    Inputs.Add(UdpClient);
    SelectedInput = selected != null && Inputs.Contains(selected)
        ? selected
        : Inputs.FirstOrDefault();
  }

  [RelayCommand]
  private async Task ToggleConnectAsync() {
    await _lifecycleGate.WaitAsync();
    Busy = true;
    try {
      if (_cts != null) {
        await StopCoreAsync("Stopped.");
      } else {
        await StartCoreAsync();
      }
    } finally {
      Busy = false;
      _lifecycleGate.Release();
    }
  }

  private async Task StartCoreAsync() {
    if (_disposed) {
      return;
    }
    if (string.IsNullOrWhiteSpace(SelectedInput)) {
      Status = "Select a serial or network input first.";
      return;
    }
    if (NetworkPort is < 1 or > 65535) {
      Status = "Network port must be between 1 and 65535.";
      return;
    }
    if (!Rates.Contains(UpdateRateHz)) {
      Status = "Select a supported update rate.";
      return;
    }
    if (IsSerialInput && !Bauds.Contains(SelectedBaud)) {
      Status = "Select a supported serial baud rate.";
      return;
    }

    NmeaVehicleTarget? target = _activeTarget(UpdateRallyPoint);
    if (target == null) {
      Status = UpdateRallyPoint
          ? "Connect and select a vehicle before enabling Rally Point 0 updates."
          : "Select a MAVLink vehicle before starting Moving Base.";
      RefreshTargetDescription();
      return;
    }
    if (UpdateRallyPoint && !await _confirmRally(target)) {
      Status = "Moving Base rally updates were cancelled.";
      return;
    }
    if (!IsTargetCurrent(target, requireOpen: UpdateRallyPoint)) {
      Status = TargetChangedMessage;
      RefreshTargetDescription();
      return;
    }

    try {
      var opened = await Task.Run(() => OpenInput(
          SelectedInput, SelectedBaud, NetworkHost.Trim(), NetworkPort));
      if (!IsTargetCurrent(target, requireOpen: UpdateRallyPoint)) {
        if (opened.Input is IDisposable openedDisposable) {
          openedDisposable.Dispose();
        } else {
          opened.Input.Close();
        }
        opened.Listener?.Stop();
        throw new InvalidOperationException(TargetChangedMessage);
      }
      _input = opened.Input;
      _listener = opened.Listener;
      var cts = new CancellationTokenSource();
      _cts = cts;
      _boundTarget = target;
      _targetInvalidated = false;
      if (_listener != null && _input is TcpSerial tcp) {
        _acceptTask = AcceptClientsAsync(_listener, tcp, cts.Token);
      }
      _readerTask = Task.Run(() => ReadLoop(target, cts.Token), cts.Token);
      PersistSettings();
      TargetDescription = "Bound to " + NmeaVehicleSession.Describe(target) + ".";
      ConnectButtonText = "Stop";
      Status = SelectedInput == TcpHost
          ? $"Listening for NMEA TCP clients on port {NetworkPort}."
          : $"Reading moving-base NMEA from {SelectedInput}.";
      OnPropertyChanged(nameof(IsRunning));
      OnPropertyChanged(nameof(CanEditSettings));
    } catch (Exception ex) {
      CloseTransport();
      _boundTarget = null;
      Status = _targetInvalidated || ex.Message == TargetChangedMessage
          ? TargetChangedMessage
          : "Moving Base connection failed: " + ex.Message;
      RefreshTargetDescription();
    }
  }

  private (ICommsSerial Input, TcpListener? Listener) OpenInput(
      string input, int baud, string host, int port) {
    switch (input) {
      case TcpHost: {
          var listener = new TcpListener(IPAddress.Any, port);
          listener.Start();
          return (new TcpSerial { ReadTimeout = 1000 }, listener);
        }
      case TcpClient: {
          if (string.IsNullOrWhiteSpace(host)) {
            throw new InvalidOperationException("Enter a remote TCP host.");
          }
          var tcp = new TcpSerial {
            Host = host,
            Port = port.ToString(CultureInfo.InvariantCulture),
            ReadTimeout = 1000,
            autoReconnect = true,
            retrys = int.MaxValue,
          };
          tcp.Open();
          return (tcp, null);
        }
      case UdpHost: {
          var udp = new UdpSerial(new System.Net.Sockets.UdpClient(port)) { ReadTimeout = 1000 };
          return (udp, null);
        }
      case UdpClient: {
          if (string.IsNullOrWhiteSpace(host)) {
            throw new InvalidOperationException("Enter a remote UDP host.");
          }
          var udp = new UdpSerialConnect { ReadTimeout = 1000 };
          udp.Open(host, port.ToString(CultureInfo.InvariantCulture));
          return (udp, null);
        }
      default: {
          var serial = new SerialPort {
            PortName = input,
            BaudRate = baud,
            ReadTimeout = 1000,
          };
          serial.Open();
          return (serial, null);
        }
    }
  }

  private async Task AcceptClientsAsync(
      TcpListener listener, TcpSerial input, CancellationToken cancellationToken) {
    while (!cancellationToken.IsCancellationRequested) {
      try {
        var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var previous = input.client;
        input.client = client;
        previous.Dispose();
        Dispatcher.UIThread.Post(() =>
            Status = "NMEA TCP client connected: " + client.Client.RemoteEndPoint);
      } catch (OperationCanceledException) {
        break;
      } catch (ObjectDisposedException) {
        break;
      } catch (Exception ex) {
        if (!cancellationToken.IsCancellationRequested) {
          Dispatcher.UIThread.Post(() => Status = "TCP accept failed: " + ex.Message);
        }
      }
    }
  }

  private void ReadLoop(
      NmeaVehicleTarget target, CancellationToken cancellationToken) {
    DateTime nextUpdate = DateTime.MinValue;
    DateTime nextRallyUpdate = DateTime.MinValue;
    string logDirectory = Settings.GetUserDataDirectory();
    Directory.CreateDirectory(logDirectory);
    string logPath = Path.Combine(logDirectory, "MovingBase.txt");
    using var log = new StreamWriter(logPath, append: true) { AutoFlush = true };

    while (!cancellationToken.IsCancellationRequested) {
      try {
        if (!IsTargetCurrent(target, requireOpen: UpdateRallyPoint)) {
          InvalidateTarget();
          return;
        }
        var input = _input;
        if (input?.IsOpen != true) {
          cancellationToken.WaitHandle.WaitOne(100);
          continue;
        }

        string line = input.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) {
          continue;
        }
        log.WriteLine(line.TrimEnd());
        if (!NmeaGgaParser.TryParse(line, out var fix, out string error)) {
          if (error == "GPS has no position fix.") {
            Dispatcher.UIThread.Post(() => Status = error);
          }
          continue;
        }

        if (DateTime.UtcNow < nextUpdate) {
          continue;
        }
        nextUpdate = DateTime.UtcNow.AddSeconds(1 / Math.Max(0.1, UpdateRateHz));
        var baseLocation = new PointLatLngAlt(
            fix.Latitude, fix.Longitude, fix.AltitudeM,
            $"Sats {fix.Satellites} hdop {fix.Hdop:0.##}");
        if (!IsTargetCurrent(target, requireOpen: UpdateRallyPoint)) {
          InvalidateTarget();
          return;
        }
        _setBase(target, baseLocation);

        double displayAlt = ShowRelativeAltitude
            ? fix.AltitudeM - target.Link.MAV.cs.HomeAlt
            : fix.AltitudeM;
        string altitudeKind = ShowRelativeAltitude ? "relative" : "AMSL";
        string label = string.Format(CultureInfo.InvariantCulture,
            "{0:0.0000000} {1:0.0000000} {2:0.0} m {3}; sats {4}, HDOP {5:0.##}",
            fix.Latitude, fix.Longitude, displayAlt, altitudeKind, fix.Satellites, fix.Hdop);
        Dispatcher.UIThread.Post(() => {
          Location = label;
          Status = "Moving-base position is active and visible on Flight Data.";
        });

        if (UpdateRallyPoint && DateTime.UtcNow >= nextRallyUpdate) {
          nextRallyUpdate = DateTime.UtcNow.AddSeconds(5);
          if (!IsTargetCurrent(target, requireOpen: true)) {
            InvalidateTarget();
            return;
          }
          try {
            _updateRally(target, baseLocation);
          } catch (Exception ex) {
            RequestStop("Moving Base rally updates stopped: " + ex.Message);
            return;
          }
        }
      } catch (TimeoutException) {
      } catch (Exception ex) when (!cancellationToken.IsCancellationRequested) {
        RequestStop("Moving Base input stopped: " + ex.Message);
        return;
      }
    }
  }

  private static void UpdateVehicleRallyPoint(
      NmeaVehicleTarget target, PointLatLngAlt baseLocation) {
    MAVLinkInterface link = target.Link;
    if (link.BaseStream?.IsOpen != true || !link.MAV.param.ContainsKey("RALLY_TOTAL")) {
      throw new InvalidOperationException(
          "the selected vehicle is disconnected or does not expose RALLY_TOTAL");
    }
    double defaultAltDisplay = LoadDouble(Settings.Instance, "TXT_DefaultAlt", 100);
    double defaultAltM = defaultAltDisplay / Math.Max(0.0001, CurrentState.multiplieralt);
    link.setParam(target.SystemId, target.ComponentId, "RALLY_TOTAL", 1);
    var rally = new PointLatLngAlt(baseLocation) { Alt = baseLocation.Alt + defaultAltM };
#pragma warning disable CS0612 // Legacy rally transport is required for MAVLink 1 vehicles.
    link.setRallyPoint(0, rally, 0, 0, 0, 1);
#pragma warning restore CS0612
  }

  public async Task StopAsync() {
    await _lifecycleGate.WaitAsync();
    try {
      await StopCoreAsync("Stopped.");
    } finally {
      _lifecycleGate.Release();
    }
  }

  private async Task StopCoreAsync(string reason) {
    var cts = _cts;
    var reader = _readerTask;
    var accept = _acceptTask;
    _cts = null;
    _readerTask = null;
    _acceptTask = null;
    cts?.Cancel();
    CloseTransport();

    foreach (Task? task in new[] { reader, accept }) {
      if (task == null) {
        continue;
      }
      try {
        await task.WaitAsync(TimeSpan.FromSeconds(2));
      } catch (OperationCanceledException) {
      } catch (TimeoutException) {
      }
    }
    cts?.Dispose();
    _boundTarget = null;
    _targetInvalidated = false;
    ConnectButtonText = "Connect";
    Status = reason;
    OnPropertyChanged(nameof(IsRunning));
    OnPropertyChanged(nameof(CanEditSettings));
    RefreshTargetDescription();
  }

  private void CloseTransport() {
    TcpListener? listener = Interlocked.Exchange(ref _listener, null);
    try {
      listener?.Stop();
    } catch {
    }
    ICommsSerial? input = Interlocked.Exchange(ref _input, null);
    try {
      input?.Close();
    } catch {
    }
    if (input is IDisposable disposable) {
      disposable.Dispose();
    }
  }

  private void OnConnectionChanged() {
    if (_disposed) {
      return;
    }
    NmeaVehicleTarget? bound = _boundTarget;
    if (bound == null) {
      Dispatcher.UIThread.Post(RefreshTargetDescription);
      return;
    }
    if (!IsTargetCurrent(bound, requireOpen: UpdateRallyPoint)) {
      InvalidateTarget();
    }
  }

  private void InvalidateTarget() {
    _targetInvalidated = true;
    _cts?.Cancel();
    CloseTransport();
    RequestStop(TargetChangedMessage);
  }

  private void RequestStop(string reason) {
    if (_disposed || Interlocked.Exchange(ref _stopScheduled, 1) != 0) {
      return;
    }
    Dispatcher.UIThread.Post(() => _ = StopForReasonAsync(reason));
  }

  private async Task StopForReasonAsync(string reason) {
    await _lifecycleGate.WaitAsync();
    try {
      if (_cts != null || _boundTarget != null) {
        await StopCoreAsync(reason);
      } else if (!_disposed) {
        Status = reason;
      }
    } finally {
      _lifecycleGate.Release();
      Interlocked.Exchange(ref _stopScheduled, 0);
    }
  }

  private bool IsTargetCurrent(NmeaVehicleTarget target, bool requireOpen) =>
      NmeaVehicleSession.ShouldContinue(
          _targetInvalidated, target, _activeTarget(requireOpen), requireOpen);

  internal void SynchronizeActiveTarget() => OnConnectionChanged();

  private void RefreshTargetDescription() {
    if (_disposed || _boundTarget != null) {
      return;
    }
    NmeaVehicleTarget? current = _activeTarget(UpdateRallyPoint);
    TargetDescription = current == null
        ? "No suitable vehicle selected."
        : "Ready for " + NmeaVehicleSession.Describe(current) + ".";
  }

  private void PersistSettings() {
    var settings = Settings.Instance;
    settings["MovingBaseInput"] = SelectedInput ?? "";
    settings["MovingBaseBaud"] = SelectedBaud.ToString(CultureInfo.InvariantCulture);
    settings["MovingBaseHost"] = NetworkHost;
    settings["MovingBasePort"] = NetworkPort.ToString(CultureInfo.InvariantCulture);
    settings["MovingBaseRate"] = UpdateRateHz.ToString(CultureInfo.InvariantCulture);
    settings["MovingBaseUpdateRally"] = UpdateRallyPoint.ToString();
    settings["MovingBaseRelativeAlt"] = ShowRelativeAltitude.ToString();
    settings.Save();
  }

  private static bool IsNetworkInput(string? value) =>
      value is TcpHost or TcpClient or UdpHost or UdpClient;

  private static int LoadInt(Settings settings, string key, int fallback) =>
      int.TryParse(settings[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
          ? value
          : fallback;

  private static double LoadDouble(Settings settings, string key, double fallback) =>
      double.TryParse(settings[key], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
          ? value
          : fallback;

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    if (_subscribedToAppState) {
      AppState.ConnectionChanged -= OnConnectionChanged;
    }
    _targetInvalidated = true;
    CancellationTokenSource? cts = _cts;
    _cts = null;
    _readerTask = null;
    _acceptTask = null;
    cts?.Cancel();
    CloseTransport();
    _boundTarget = null;
    cts?.Dispose();
  }

  private const string TargetChangedMessage =
      "The active modem or vehicle changed or disconnected. Moving Base was stopped; start it "
      + "again only after verifying the selected target.";
}
