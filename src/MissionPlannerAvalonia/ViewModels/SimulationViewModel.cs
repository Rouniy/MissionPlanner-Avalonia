using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class SimulationViewModel : ViewModelBase {
  private readonly SitlLauncher _sitl = new();
  private readonly List<SitlLauncher> _swarmLaunchers = [];
  private readonly List<MavLinkConnection> _swarmConnections = [];
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private readonly ConnectionViewModel _connection;
  private bool _singleOwnsPrimaryConnection;

  public event Action? RequestFlightData;

  [ObservableProperty]
  private string _status = "Select a firmware to simulate, then press Start.";

  [ObservableProperty]
  private string _log = "";

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
  [NotifyCanExecuteChangedFor(nameof(StartCopterSingleLinkSwarmCommand))]
  [NotifyCanExecuteChangedFor(nameof(StartCopterMultiLinkSwarmCommand))]
  [NotifyCanExecuteChangedFor(nameof(StartPlaneMultiLinkSwarmCommand))]
  [NotifyCanExecuteChangedFor(nameof(StartRoverMultiLinkSwarmCommand))]
  private bool _isBusy;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(StartCopterSingleLinkSwarmCommand))]
  [NotifyCanExecuteChangedFor(nameof(StartCopterMultiLinkSwarmCommand))]
  [NotifyCanExecuteChangedFor(nameof(StartPlaneMultiLinkSwarmCommand))]
  [NotifyCanExecuteChangedFor(nameof(StartRoverMultiLinkSwarmCommand))]
  private bool _isRunning;

  [ObservableProperty]
  private string _startStopText = "Start";

  [ObservableProperty]
  private bool _isPlane = true;

  [ObservableProperty]
  private bool _isRover;

  [ObservableProperty]
  private bool _isCopter;

  [ObservableProperty]
  private bool _isHeli;

  [ObservableProperty]
  private double _homeLat;

  [ObservableProperty]
  private double _homeLng;

  [ObservableProperty]
  private double _homeAlt;

  [ObservableProperty]
  private int _heading;

  [ObservableProperty]
  private int _simSpeed = 1;

  [ObservableProperty]
  private string _selectedModel = "";

  [ObservableProperty]
  private string _extraCmdline = "";

  [ObservableProperty]
  private bool _wipeEeprom;

  [ObservableProperty]
  private int _selectedChannelIndex;

  [ObservableProperty]
  private int _swarmCount = 10;

  public IReadOnlyList<string> Models { get; } = new[] {
    "", "quadplane", "xplane", "xplane-heli", "firefly", "+", "quad", "copter", "x",
    "hexa", "octa", "tri", "y6", "heli", "heli-dual", "heli-compound", "singlecopter",
    "coaxcopter", "rover", "crrcsim", "jsbsim", "flightaxis", "gazebo", "last_letter",
    "tracker", "balloon", "plane", "calibration", "plane-jet", "sailboat", "motorboat",
    "morse-rover", "rover-skid", "plane-3d",
  };

  public IReadOnlyList<string> Channels { get; } = new[] {
    "Latest (Dev)", "Beta", "Stable", "Skip Download",
  };

  internal SimulationViewModel(ConnectionViewModel connection) {
    _connection = connection;
    _sitl.Log += OnLog;

    try {
      SelectedChannelIndex = Settings.Instance.GetInt32("sitl_download_version");
    } catch {
      SelectedChannelIndex = 0;
    }

    if (SelectedChannelIndex < 0 || SelectedChannelIndex >= Channels.Count) {
      SelectedChannelIndex = 0;
    }

    InitHome();

    if (!SitlSupported) {
      Status = "No prebuilt SITL for macOS. Build from source or run under Linux/WSL.";
    }
  }

  public bool SitlSupported => SitlLauncher.PlatformSupported;

  private void InitHome() {
    var planned = _comPort.MAV?.cs?.PlannedHomeLocation;
    if (planned != null && (planned.Lat != 0 || planned.Lng != 0)) {
      SetHome(planned.Lat, planned.Lng);
    } else {
      SetHome(-35.3633515, 149.1652412);
    }
  }

  public void SetHome(double lat, double lng) {
    HomeLat = lat;
    HomeLng = lng;
    try {
      HomeAlt = srtm.getAltitude(lat, lng).alt;
    } catch {
      HomeAlt = 0;
    }
  }

  private SitlVehicle SelectedVehicle =>
      IsCopter ? SitlVehicle.Copter :
      IsRover ? SitlVehicle.Rover :
      IsHeli ? SitlVehicle.Heli :
      SitlVehicle.Plane;

  private SitlChannel SelectedChannel => SelectedChannelIndex switch {
    1 => SitlChannel.Beta,
    2 => SitlChannel.Stable,
    3 => SitlChannel.Skip,
    _ => SitlChannel.Dev,
  };

  private string BuildHome() => string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
      HomeLat, HomeLng, HomeAlt, Heading);

  private void OnLog(string line) => Dispatcher.UIThread.Post(() => {
    Log += line + "\n";
    Status = line;
  });

  [RelayCommand(CanExecute = nameof(CanStartStop))]
  private async Task StartStop() {
    if (IsRunning) {
      IsBusy = true;
      StartStopText = "Stopping…";
      try {
        await StopAllSimulationAsync();
        Status = "SITL stopped.";
      } finally {
        IsRunning = false;
        IsBusy = false;
        StartStopText = "Start";
      }
      return;
    }

    try {
      Settings.Instance["sitl_download_version"] = SelectedChannelIndex.ToString();
    } catch {

    }

    IsBusy = true;
    StartStopText = "Starting…";
    try {
      var opts = new SitlStartOptions {
        Vehicle = SelectedVehicle,
        Channel = SelectedChannel,
        Model = SelectedModel,
        Home = BuildHome(),
        Speed = SimSpeed,
        ExtraCmdline = ExtraCmdline,
        WipeEeprom = WipeEeprom,
      };

      bool ok = await _sitl.StartAsync(opts);
      if (!ok) {
        Status = "SITL did not start (no prebuilt binary on this platform/channel?). See log.";
        StartStopText = "Start";
        return;
      }

      Status = $"Connecting to {_sitl.TcpEndpoint} …";
      bool connected = await ConnectAsync();
      _sitl.SetAsPrimaryConnection(connected);
      _singleOwnsPrimaryConnection = connected;
      IsRunning = true;
      StartStopText = "Stop";
      Status = connected
          ? $"SITL running and connected on {_sitl.TcpEndpoint}."
          : $"SITL running on {_sitl.TcpEndpoint}. Auto-connect failed, connect manually.";

      if (connected) {
        RequestFlightData?.Invoke();
      }
    } catch (Exception ex) {
      _sitl.Stop();
      _singleOwnsPrimaryConnection = false;
      Status = "Start error: " + ex.Message;
      StartStopText = "Start";
    } finally {
      IsBusy = false;
    }
  }

  private bool CanStartStop() => !IsBusy && SitlSupported;

  private bool CanStartSwarm() => !IsBusy && !IsRunning && SitlSupported;

  [RelayCommand(CanExecute = nameof(CanStartSwarm))]
  private Task StartCopterSingleLinkSwarm() =>
      StartSwarmAsync(SitlVehicle.Copter, chained: true);

  [RelayCommand(CanExecute = nameof(CanStartSwarm))]
  private Task StartCopterMultiLinkSwarm() =>
      StartSwarmAsync(SitlVehicle.Copter, chained: false);

  [RelayCommand(CanExecute = nameof(CanStartSwarm))]
  private Task StartPlaneMultiLinkSwarm() =>
      StartSwarmAsync(SitlVehicle.Plane, chained: false);

  [RelayCommand(CanExecute = nameof(CanStartSwarm))]
  private Task StartRoverMultiLinkSwarm() =>
      StartSwarmAsync(SitlVehicle.Rover, chained: false);

  private async Task StartSwarmAsync(SitlVehicle vehicle, bool chained) {
    if (SwarmCount is < 2 or > 50) {
      Status = "Swarm count must be between 2 and 50.";
      return;
    }
    if (ExtraCmdline.Contains("--defaults", StringComparison.OrdinalIgnoreCase)) {
      Status = "Remove custom --defaults before starting a swarm; each instance needs identity.parm.";
      return;
    }

    try {
      Settings.Instance["sitl_download_version"] = SelectedChannelIndex.ToString();
    } catch {

    }

    IsBusy = true;
    StartStopText = "Starting swarm…";
    try {
      IReadOnlyList<SitlSwarmInstancePlan> plans = SitlLauncher.BuildSwarmPlan(
          HomeLat, HomeLng, HomeAlt, Heading, SwarmCount, chained);
      ConnectionListEndpoint[] endpoints = [.. (chained
          ? plans.Where(plan => plan.Instance == 0)
          : plans.OrderBy(plan => plan.Instance))
          .Select(plan => new ConnectionListEndpoint(
              ConnectionListTransport.TcpClient,
              "127.0.0.1",
              SitlLauncher.TcpPortForInstance(plan.Instance),
              "",
              0,
              plan.Instance + 1))];
      ConnectionListEndpoint? duplicate = endpoints.FirstOrDefault(endpoint =>
          AppState.Connections.ContainsEndpoint(endpoint.Canonical));
      if (duplicate != null) {
        throw new InvalidOperationException(
            $"Connection {duplicate.DisplayName} is already open.");
      }

      foreach (SitlSwarmInstancePlan plan in plans) {
        var launcher = new SitlLauncher();
        launcher.Log += OnLog;
        _swarmLaunchers.Add(launcher);
        bool started = await launcher.StartAsync(new SitlStartOptions {
          Vehicle = vehicle,
          Channel = SelectedChannel,
          Home = plan.Home,
          Speed = SimSpeed,
          ExtraCmdline = ExtraCmdline,
          WipeEeprom = WipeEeprom,
          Instance = plan.Instance,
          SystemId = plan.SystemId,
          UseIdentityParameters = true,
          SecondarySerialClientPort = plan.SecondarySerialClientPort,
        });
        if (!started) {
          throw new InvalidOperationException(
              $"SITL instance {plan.Instance} did not start. See the simulation log.");
        }
      }

      OnLog($"Opening {endpoints.Length} SITL telemetry link(s)…");
      ConnectionListOpenResult opened = await ConnectionListService.OpenEndpointsAsync(
          endpoints, AppState.Connections,
          progress: (_, message) => OnLog(message));
      _swarmConnections.AddRange(opened.Opened);
      if (opened.Failures.Count > 0 || opened.Opened.Count != endpoints.Length) {
        string details = string.Join("; ", opened.Failures.Select(failure =>
            $"{failure.Endpoint.DisplayName}: {failure.Message}"));
        throw new IOException("Could not open every SITL link" +
            (details.Length == 0 ? "." : ": " + details));
      }

      MavLinkConnection active = opened.Opened[0];
      AppState.Connections.SetActive(active);
      _connection.RefreshManagedConnections(reloadActiveParameters: true);
      AppState.RaiseConnectionChanged();
      SitlLauncher primaryLauncher = _swarmLaunchers.Single(launcher => launcher.TcpPort == 5760);
      primaryLauncher.SetAsPrimaryConnection(true);

      IsRunning = true;
      StartStopText = "Stop all";
      string linkMode = chained ? "single link" : $"{opened.Opened.Count} links";
      Status = $"{vehicle} swarm running: {plans.Count} instances, {linkMode}.";
      RequestFlightData?.Invoke();
    } catch (Exception ex) {
      await CleanupSwarmAsync();
      IsRunning = false;
      StartStopText = "Start";
      Status = "Swarm start error: " + ex.Message;
    } finally {
      IsBusy = false;
    }
  }

  private async Task<bool> ConnectAsync() {
    if (AppState.Connections.Primary.IsOpen) {
      OnLog("Another vehicle connection is already active; SITL was not made the primary link.");
      return false;
    }

    AppState.CommsSettings["TCP_host"] = "127.0.0.1";
    AppState.CommsSettings["TCP_port"] = _sitl.TcpPort.ToString(CultureInfo.InvariantCulture);
    var result = await _connection.ConnectPreparedStreamAsync(
        new TcpSerial(), "SITL", getParams: true);
    if (!result.Connected) {
      OnLog("Connect error: " + result.Error);
    }
    return result.Connected;
  }

  private async Task DisconnectAsync() {
    if (_singleOwnsPrimaryConnection && AppState.Connections.Primary.IsOpen) {
      await _connection.DisconnectAsync("SITL disconnected.");
    }
    _singleOwnsPrimaryConnection = false;
  }

  private async Task CleanupSwarmAsync() {
    AppState.ParameterLoads.CancelCurrent();
    foreach (MavLinkConnection connection in _swarmConnections.ToArray()) {
      await AppState.Connections.RemoveAsync(connection);
    }
    _swarmConnections.Clear();
    foreach (SitlLauncher launcher in _swarmLaunchers) {
      launcher.Stop();
      launcher.Log -= OnLog;
    }
    _swarmLaunchers.Clear();
    _connection.RefreshManagedConnections();
    AppState.RaiseConnectionChanged();
  }

  private async Task StopAllSimulationAsync() {
    await CleanupSwarmAsync();
    await DisconnectAsync();
    _sitl.Stop();
  }
}
