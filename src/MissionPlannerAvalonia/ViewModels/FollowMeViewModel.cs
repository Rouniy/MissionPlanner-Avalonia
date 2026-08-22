using System;
using System.Collections.ObjectModel;
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

public partial class FollowMeViewModel : ViewModelBase, IDisposable {
  private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
  private readonly object _positionGate = new();
  private readonly Func<NmeaVehicleTarget?> _activeTarget;
  private readonly Func<string, int, ICommsSerial> _openSerial;
  private readonly Action<NmeaVehicleTarget, Locationwp, bool> _sendGuided;
  private readonly Func<NmeaVehicleTarget, string, Task<bool>> _confirmStart;
  private readonly bool _subscribedToAppState;
  private CancellationTokenSource? _cts;
  private ICommsSerial? _gps;
  private Task? _readerTask;
  private Task? _senderTask;
  private NmeaVehicleTarget? _boundTarget;
  private FollowPosition? _position;
  private volatile bool _targetInvalidated;
  private int _stopScheduled;
  private bool _guidedSet;
  private bool _disposed;

  public FollowMeViewModel()
      : this(
          () => NmeaVehicleSession.CaptureActive(requireOpen: true),
          OpenSerial,
          static (target, waypoint, setGuided) => target.Link.setGuidedModeWP(
              target.SystemId, target.ComponentId, waypoint, setGuided),
          static (target, source) => Dialogs.ConfirmDangerous(
              "Start Follow Me",
              $"Follow Me will repeatedly command {NmeaVehicleSession.Describe(target)} to move "
              + $"toward {source}. Verify GUIDED flight, altitude, surrounding airspace and the "
              + "selected modem before continuing.",
              "Start Follow Me"),
          subscribeToAppState: true) {
  }

  internal FollowMeViewModel(
      Func<NmeaVehicleTarget?> activeTarget,
      Func<string, int, ICommsSerial> openSerial,
      Action<NmeaVehicleTarget, Locationwp, bool> sendGuided,
      Func<NmeaVehicleTarget, string, Task<bool>> confirmStart,
      bool subscribeToAppState = false) {
    _activeTarget = activeTarget;
    _openSerial = openSerial;
    _sendGuided = sendGuided;
    _confirmStart = confirmStart;
    _subscribedToAppState = subscribeToAppState;
    RefreshPorts();
    SelectedBaud = 4800;
    UpdateRateHz = 0.5;
    RelativeAltM = 100;
    RefreshTargetDescription();
    if (_subscribedToAppState) {
      AppState.ConnectionChanged += OnConnectionChanged;
    }
  }

  public ObservableCollection<string> Ports { get; } = new();

  public ObservableCollection<int> Bauds { get; } = new() {
      4800, 9600, 14400, 19200, 28800, 38400, 57600, 115200,
  };

  public ObservableCollection<double> Rates { get; } = new() { 0.25, 0.5, 1, 2 };

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanEditSettings))]
  private bool _busy;

  [ObservableProperty]
  private bool _useSerialGps;

  [ObservableProperty]
  private string? _selectedPort;

  [ObservableProperty]
  private int _selectedBaud;

  [ObservableProperty]
  private double _updateRateHz;

  [ObservableProperty]
  private double _relativeAltM;

  [ObservableProperty]
  private double _manualLat;

  [ObservableProperty]
  private double _manualLng;

  [ObservableProperty]
  private string _status = "Stopped.";

  [ObservableProperty]
  private string _connectButtonText = "Start";

  [ObservableProperty]
  private string _locationLabel = "No target position received.";

  [ObservableProperty]
  private string _targetDescription = "No connected vehicle selected.";

  public bool IsRunning => _cts != null;
  public bool CanEditSettings => !Busy && !IsRunning;

  partial void OnManualLatChanged(double value) => UpdateManualPosition();
  partial void OnManualLngChanged(double value) => UpdateManualPosition();
  partial void OnRelativeAltMChanged(double value) => UpdateManualPosition();

  private void UpdateManualPosition() {
    if (UseSerialGps || !TryValidatePosition(ManualLat, ManualLng, RelativeAltM, out _)) {
      return;
    }
    lock (_positionGate) {
      _position = new FollowPosition(
          ManualLat, ManualLng, RelativeAltM, "manual", DateTimeOffset.UtcNow, IsSerial: false);
    }
  }

  [RelayCommand]
  private void RefreshPorts() {
    string? selected = SelectedPort;
    Ports.Clear();
    foreach (string port in SerialPort.GetPortNames().Distinct().OrderBy(item => item)) {
      Ports.Add(port);
    }
    SelectedPort = selected != null && Ports.Contains(selected)
        ? selected
        : Ports.FirstOrDefault();
  }

  [RelayCommand]
  private void UseGcsLocation() {
    UseSerialGps = false;
    if (!TryValidatePosition(ManualLat, ManualLng, RelativeAltM, out string error)) {
      Status = error;
      return;
    }
    UpdateManualPosition();
    Status = $"Manual target set to {ManualLat:0.0000000}, {ManualLng:0.0000000}.";
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
    if (!Rates.Contains(UpdateRateHz)) {
      Status = "Select a supported update rate.";
      return;
    }
    if (!double.IsFinite(RelativeAltM) || RelativeAltM <= 0 || RelativeAltM > 10000) {
      Status = "Relative altitude must be greater than zero and no more than 10000 m.";
      return;
    }
    if (UseSerialGps && (string.IsNullOrWhiteSpace(SelectedPort) || !Bauds.Contains(SelectedBaud))) {
      Status = "Select a GPS serial port and supported baud rate first.";
      return;
    }
    if (!UseSerialGps
        && !TryValidatePosition(ManualLat, ManualLng, RelativeAltM, out string positionError)) {
      Status = positionError;
      return;
    }

    NmeaVehicleTarget? target = _activeTarget();
    if (target == null) {
      Status = "Connect and select a vehicle before starting Follow Me.";
      RefreshTargetDescription();
      return;
    }
    string source = UseSerialGps
        ? $"NMEA GPS on {SelectedPort} at {SelectedBaud} baud"
        : $"the fixed target {ManualLat:0.0000000}, {ManualLng:0.0000000} at {RelativeAltM:0.#} m";
    if (!await _confirmStart(target, source)) {
      Status = "Follow Me start cancelled.";
      return;
    }
    if (!IsTargetCurrent(target)) {
      Status = TargetChangedMessage;
      RefreshTargetDescription();
      return;
    }

    ICommsSerial? opened = null;
    try {
      if (UseSerialGps) {
        string port = SelectedPort!;
        int baud = SelectedBaud;
        Status = $"Opening NMEA GPS {port} at {baud} baud…";
        opened = await Task.Run(() => _openSerial(port, baud));
        if (!IsTargetCurrent(target)) {
          throw new InvalidOperationException(TargetChangedMessage);
        }
      } else {
        UpdateManualPosition();
      }

      var cts = new CancellationTokenSource();
      _boundTarget = target;
      _targetInvalidated = false;
      _guidedSet = false;
      _gps = opened;
      _cts = cts;
      if (opened != null) {
        ICommsSerial activeGps = opened;
        _readerTask = Task.Run(() => ReadGpsLoop(activeGps, cts.Token), cts.Token);
      }
      _senderTask = Task.Run(() => SendLoop(target, cts.Token), cts.Token);
      opened = null;
      TargetDescription = "Bound to " + NmeaVehicleSession.Describe(target) + ".";
      ConnectButtonText = "Stop";
      Status = UseSerialGps
          ? $"Waiting for a valid GGA fix from {SelectedPort}."
          : "Follow Me is sending the confirmed manual target.";
      NotifyRunningState();
    } catch (Exception ex) {
      CloseInput(opened);
      _boundTarget = null;
      Status = _targetInvalidated || ex.Message == TargetChangedMessage
          ? TargetChangedMessage
          : "Follow Me start failed: " + ex.Message;
      RefreshTargetDescription();
    }
  }

  private void ReadGpsLoop(ICommsSerial gps, CancellationToken cancellationToken) {
    try {
      while (!cancellationToken.IsCancellationRequested) {
        string line;
        try {
          line = gps.ReadLine();
        } catch (TimeoutException) {
          continue;
        }
        if (string.IsNullOrWhiteSpace(line)) {
          continue;
        }
        if (!NmeaGgaParser.TryParse(line, out NmeaGgaFix fix, out string error)) {
          if (error == "GPS has no position fix.") {
            lock (_positionGate) {
              _position = null;
            }
            PostStatus("GPS has no position fix; GUIDED updates are withheld.");
          }
          continue;
        }
        var position = new FollowPosition(
            fix.Latitude, fix.Longitude, RelativeAltM,
            $"sats {fix.Satellites}, HDOP {fix.Hdop:0.##}",
            DateTimeOffset.UtcNow, IsSerial: true);
        lock (_positionGate) {
          _position = position;
        }
        PostLocation(position);
      }
    } catch (Exception ex) when (!cancellationToken.IsCancellationRequested) {
      RequestStop("Follow Me GPS input stopped: " + ex.Message);
    }
  }

  private async Task SendLoop(
      NmeaVehicleTarget target, CancellationToken cancellationToken) {
    try {
      while (true) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTargetCurrent(target)) {
          InvalidateTarget();
          return;
        }

        FollowPosition? position;
        lock (_positionGate) {
          position = _position;
        }
        if (position == null) {
          PostStatus("Waiting for a valid target position; no GUIDED update was sent.");
        } else if (position.IsSerial
                   && DateTimeOffset.UtcNow - position.ReceivedAt > MaxFixAge(UpdateRateHz)) {
          PostStatus("The NMEA fix is stale; GUIDED updates are withheld until a fresh fix arrives.");
        } else {
          var waypoint = new Locationwp {
            id = (ushort)MAVLink.MAV_CMD.WAYPOINT,
            alt = (float)position.AltitudeM,
            lat = position.Latitude,
            lng = position.Longitude,
          };
          if (!IsTargetCurrent(target)) {
            InvalidateTarget();
            return;
          }
          if (target.Link.giveComport) {
            PostStatus("MAVLink link is busy; GUIDED update withheld.");
            await Task.Delay(UpdateInterval(UpdateRateHz), cancellationToken).ConfigureAwait(false);
            continue;
          }
          _sendGuided(target, waypoint, !_guidedSet);
          _guidedSet = true;
          PostLocation(position);
          PostStatus("Follow Me target sent to " + NmeaVehicleSession.Describe(target) + ".");
        }

        await Task.Delay(UpdateInterval(UpdateRateHz), cancellationToken).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (Exception ex) {
      RequestStop("Follow Me command stream stopped: " + ex.Message);
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
    if (!IsTargetCurrent(bound)) {
      InvalidateTarget();
    }
  }

  private void InvalidateTarget() {
    _targetInvalidated = true;
    _cts?.Cancel();
    CloseInput(_gps);
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

  private async Task StopCoreAsync(string reason) {
    CancellationTokenSource? cts = _cts;
    Task? reader = _readerTask;
    Task? sender = _senderTask;
    ICommsSerial? gps = _gps;
    _cts = null;
    _readerTask = null;
    _senderTask = null;
    _gps = null;
    cts?.Cancel();
    CloseInput(gps);
    foreach (Task? task in new[] { reader, sender }) {
      if (task == null) {
        continue;
      }
      try {
        await task.WaitAsync(TimeSpan.FromSeconds(2));
      } catch (OperationCanceledException) {
      } catch (TimeoutException) {
      } catch {
      }
    }
    cts?.Dispose();
    _boundTarget = null;
    _targetInvalidated = false;
    _guidedSet = false;
    lock (_positionGate) {
      _position = null;
    }
    ConnectButtonText = "Start";
    Status = reason;
    NotifyRunningState();
    RefreshTargetDescription();
  }

  public async Task StopAsync() {
    await _lifecycleGate.WaitAsync();
    try {
      if (_cts != null || _boundTarget != null) {
        await StopCoreAsync("Stopped.");
      }
    } finally {
      _lifecycleGate.Release();
    }
  }

  private bool IsTargetCurrent(NmeaVehicleTarget target) =>
      NmeaVehicleSession.ShouldContinue(
          _targetInvalidated, target, _activeTarget(), requireOpen: true);

  internal void SynchronizeActiveTarget() => OnConnectionChanged();

  internal static TimeSpan UpdateInterval(double rateHz) =>
      TimeSpan.FromSeconds(1 / Math.Clamp(rateHz, 0.1, 20));

  internal static TimeSpan MaxFixAge(double rateHz) =>
      TimeSpan.FromSeconds(Math.Max(5, 3 / Math.Clamp(rateHz, 0.1, 20)));

  internal static bool TryValidatePosition(
      double latitude, double longitude, double altitudeM, out string error) {
    if (!double.IsFinite(latitude) || latitude is < -90 or > 90) {
      error = "Latitude must be a finite value between -90 and 90 degrees.";
      return false;
    }
    if (!double.IsFinite(longitude) || longitude is < -180 or > 180) {
      error = "Longitude must be a finite value between -180 and 180 degrees.";
      return false;
    }
    if (!double.IsFinite(altitudeM) || altitudeM <= 0 || altitudeM > 10000) {
      error = "Relative altitude must be greater than zero and no more than 10000 m.";
      return false;
    }
    error = "";
    return true;
  }

  private static ICommsSerial OpenSerial(string port, int baud) {
    var input = new SerialPort {
      PortName = port,
      BaudRate = baud,
      ReadTimeout = 1000,
    };
    try {
      input.Open();
      return input;
    } catch {
      input.Dispose();
      throw;
    }
  }

  private static void CloseInput(ICommsSerial? input) {
    if (input == null) {
      return;
    }
    try {
      if (input.IsOpen) {
        input.Close();
      }
    } catch {
    }
    (input as IDisposable)?.Dispose();
  }

  private void RefreshTargetDescription() {
    if (_disposed || _boundTarget != null) {
      return;
    }
    NmeaVehicleTarget? current = _activeTarget();
    TargetDescription = current == null
        ? "No connected vehicle selected."
        : "Ready for " + NmeaVehicleSession.Describe(current) + ".";
  }

  private void PostStatus(string status) => Dispatcher.UIThread.Post(() => {
    if (!_disposed && _cts != null) {
      Status = status;
    }
  });

  private void PostLocation(FollowPosition position) => Dispatcher.UIThread.Post(() => {
    if (!_disposed && _cts != null) {
      LocationLabel = $"{position.Latitude:0.0000000} {position.Longitude:0.0000000} "
          + $"{position.AltitudeM:0.#} m; {position.Label}";
    }
  });

  private void NotifyRunningState() {
    OnPropertyChanged(nameof(IsRunning));
    OnPropertyChanged(nameof(CanEditSettings));
  }

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
    _senderTask = null;
    _boundTarget = null;
    cts?.Cancel();
    CloseInput(_gps);
    _gps = null;
    cts?.Dispose();
  }

  private sealed record FollowPosition(
      double Latitude,
      double Longitude,
      double AltitudeM,
      string Label,
      DateTimeOffset ReceivedAt,
      bool IsSerial);

  private const string TargetChangedMessage =
      "The active modem or vehicle changed or disconnected. Follow Me was stopped; start it "
      + "again only after verifying the selected target.";
}
