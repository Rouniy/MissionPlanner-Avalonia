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

  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
  private CancellationTokenSource? _cts;
  private ICommsSerial? _input;
  private TcpListener? _listener;
  private Task? _readerTask;
  private Task? _acceptTask;
  private bool _disposed;

  public MovingBaseViewModel() {
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

  public bool IsRunning => _cts != null;
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
    try {
      if (_cts != null) {
        await StopCoreAsync();
      } else {
        await StartCoreAsync();
      }
    } finally {
      _lifecycleGate.Release();
    }
  }

  private async Task StartCoreAsync() {
    if (string.IsNullOrWhiteSpace(SelectedInput)) {
      Status = "Select a serial or network input first.";
      return;
    }
    if (NetworkPort is < 1 or > 65535) {
      Status = "Network port must be between 1 and 65535.";
      return;
    }

    try {
      var opened = await Task.Run(() => OpenInput(
          SelectedInput, SelectedBaud, NetworkHost.Trim(), NetworkPort));
      _input = opened.Input;
      _listener = opened.Listener;
      var cts = new CancellationTokenSource();
      _cts = cts;
      if (_listener != null && _input is TcpSerial tcp) {
        _acceptTask = AcceptClientsAsync(_listener, tcp, cts.Token);
      }
      _readerTask = Task.Run(() => ReadLoop(cts.Token), cts.Token);
      PersistSettings();
      ConnectButtonText = "Stop";
      Status = SelectedInput == TcpHost
          ? $"Listening for NMEA TCP clients on port {NetworkPort}."
          : $"Reading moving-base NMEA from {SelectedInput}.";
      OnPropertyChanged(nameof(IsRunning));
    } catch (Exception ex) {
      CloseTransport();
      Status = "Moving Base connection failed: " + ex.Message;
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

  private void ReadLoop(CancellationToken cancellationToken) {
    DateTime nextUpdate = DateTime.MinValue;
    DateTime nextRallyUpdate = DateTime.MinValue;
    string logDirectory = Settings.GetUserDataDirectory();
    Directory.CreateDirectory(logDirectory);
    string logPath = Path.Combine(logDirectory, "MovingBase.txt");
    using var log = new StreamWriter(logPath, append: true) { AutoFlush = true };

    while (!cancellationToken.IsCancellationRequested) {
      try {
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
        _comPort.MAV.cs.Base = baseLocation;

        double displayAlt = ShowRelativeAltitude
            ? fix.AltitudeM - _comPort.MAV.cs.HomeAlt
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
          UpdateVehicleRallyPoint(baseLocation);
        }
      } catch (Exception ex) when (!cancellationToken.IsCancellationRequested) {
        Dispatcher.UIThread.Post(() => Status = "Moving Base input warning: " + ex.Message);
        cancellationToken.WaitHandle.WaitOne(
            TimeSpan.FromSeconds(1 / Math.Max(0.1, UpdateRateHz)));
      }
    }
  }

  private void UpdateVehicleRallyPoint(PointLatLngAlt baseLocation) {
    if (_comPort.BaseStream?.IsOpen != true || !_comPort.MAV.param.ContainsKey("RALLY_TOTAL")) {
      return;
    }
    try {
      double defaultAltDisplay = LoadDouble(Settings.Instance, "TXT_DefaultAlt", 100);
      double defaultAltM = defaultAltDisplay / Math.Max(0.0001, CurrentState.multiplieralt);
      _comPort.setParam(_comPort.MAV.sysid, _comPort.MAV.compid, "RALLY_TOTAL", 1);
      var rally = new PointLatLngAlt(baseLocation) { Alt = baseLocation.Alt + defaultAltM };
#pragma warning disable CS0612 // Legacy rally transport is required for MAVLink 1 vehicles.
      _comPort.setRallyPoint(0, rally, 0, 0, 0, 1);
#pragma warning restore CS0612
    } catch (Exception ex) {
      Dispatcher.UIThread.Post(() => Status = "Moving Base rally update failed: " + ex.Message);
    }
  }

  public async Task StopAsync() {
    await _lifecycleGate.WaitAsync();
    try {
      await StopCoreAsync();
    } finally {
      _lifecycleGate.Release();
    }
  }

  private async Task StopCoreAsync() {
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
    ConnectButtonText = "Connect";
    Status = "Stopped.";
    OnPropertyChanged(nameof(IsRunning));
  }

  private void CloseTransport() {
    try {
      _listener?.Stop();
    } catch {
    }
    _listener = null;
    try {
      _input?.Close();
    } catch {
    }
    if (_input is IDisposable disposable) {
      disposable.Dispose();
    }
    _input = null;
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
    _cts?.Cancel();
    CloseTransport();
    _lifecycleGate.Dispose();
  }
}
