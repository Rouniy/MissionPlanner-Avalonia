using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class FormationControlViewModel : ViewModelBase, IDisposable {
  private readonly Func<IReadOnlyList<FormationVehicleSource>> _snapshot;
  private readonly IFormationCommandSink _sink;
  private readonly Func<string, string, string, Task<bool>> _confirm;
  private readonly DispatcherTimer _statusTimer;
  private CancellationTokenSource? _runCancellation;
  private Task<string>? _runTask;
  private bool _refreshing;
  private bool _disposed;

  public FormationControlViewModel() : this(
      () => FormationVehicleDiscovery.Snapshot(AppState.Connections),
      new MavlinkFormationCommandSink(),
      Dialogs.ConfirmDangerous) {
  }

  internal FormationControlViewModel(
      Func<IReadOnlyList<FormationVehicleSource>> snapshot,
      IFormationCommandSink sink,
      Func<string, string, string, Task<bool>> confirm) {
    _snapshot = snapshot;
    _sink = sink;
    _confirm = confirm;
    _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    _statusTimer.Tick += (_, _) => UpdateLiveStatus();
    _statusTimer.Start();
    AppState.Connections.Changed += OnConnectionsChanged;
    RefreshVehicles(stopRunning: false);
  }

  public ObservableCollection<FormationVehicleItem> Vehicles { get; } = [];

  [ObservableProperty]
  private FormationVehicleItem? _selectedLeader;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(RunButtonText))]
  private bool _isRunning;

  [ObservableProperty]
  private bool _busy;

  [ObservableProperty]
  private bool _alignYaw = true;

  [ObservableProperty]
  private bool _aimGimbals;

  [ObservableProperty]
  private double _takeoffAltitudeM = 5;

  [ObservableProperty]
  private string _status =
      "Select a leader, explicitly enable followers, then capture or edit their offsets.";

  public string RunButtonText => IsRunning ? "Stop Formation" : "Start Formation";

  partial void OnAlignYawChanged(bool value) =>
      StopBecausePlanChanged("Formation stopped because yaw alignment changed.");

  partial void OnAimGimbalsChanged(bool value) =>
      StopBecausePlanChanged("Formation stopped because the gimbal option changed.");

  partial void OnSelectedLeaderChanged(
      FormationVehicleItem? oldValue, FormationVehicleItem? newValue) {
    if (_refreshing || newValue == null || ReferenceEquals(oldValue, newValue)) {
      UpdateRoles();
      return;
    }
    double originX = newValue.X;
    double originY = newValue.Y;
    double originZ = newValue.Z;
    foreach (FormationVehicleItem vehicle in Vehicles) {
      vehicle.SetOffset(
          vehicle.X - originX, vehicle.Y - originY, vehicle.Z - originZ,
          notifyChanged: false);
    }
    if (oldValue != null) {
      oldValue.Included = false;
    }
    newValue.Included = true;
    UpdateRoles();
    StopBecausePlanChanged("Formation stopped because the leader changed.");
  }

  [RelayCommand]
  private void Refresh() => RefreshVehicles(stopRunning: true);

  [RelayCommand]
  private void CaptureOffsets() {
    StopBecausePlanChanged("Formation stopped before capturing live offsets.");
    FormationVehicleItem? leaderRow = SelectedLeader;
    if (leaderRow == null) {
      Status = "Choose a leader before capturing offsets.";
      return;
    }
    if (!TryResolveFresh(leaderRow.Id, out FormationVehicleSource leader, out string error) ||
        !FormationCommandRunner.HasPosition(leader.State)) {
      Status = "Cannot capture offsets: " + (string.IsNullOrWhiteSpace(error)
          ? "leader position is unavailable."
          : error);
      return;
    }

    IReadOnlyList<FormationVehicleSource> sources = _snapshot();
    DateTime now = DateTime.UtcNow;
    int updated = 0;
    int skipped = 0;
    foreach (FormationVehicleItem row in Vehicles.Where(
                 row => !row.IsLeader && row.Included && row.IsEligible)) {
      if (!FormationCommandRunner.TryResolve(
              sources, row.Id, now, out FormationVehicleSource follower, out _) ||
          !FormationCommandRunner.HasPosition(follower.State)) {
        skipped++;
        continue;
      }
      FormationOffset offset = FormationGeometry.OffsetFromLeader(
          leader.State.cs.lat, leader.State.cs.lng, leader.State.cs.alt, leader.State.cs.yaw,
          follower.State.cs.lat, follower.State.cs.lng, follower.State.cs.alt);
      if (Math.Abs(offset.X) > 200 || Math.Abs(offset.Y) > 200) {
        skipped++;
        continue;
      }
      row.SetOffset(Math.Round(offset.X, 1), Math.Round(offset.Y, 1),
          Math.Round(offset.Z, 1), notifyChanged: false);
      updated++;
    }
    Status = $"Captured {updated} follower offset(s) from live positions" +
        (skipped == 0 ? "." : $"; skipped {skipped} missing or >200 m target(s).");
  }

  [RelayCommand]
  private async Task ToggleRun() {
    if (IsRunning) {
      StopBecausePlanChanged("Formation stopped by operator.");
      return;
    }
    if (_runTask is { IsCompleted: false }) {
      Status = "The previous formation stream is still stopping; wait for it to finish.";
      return;
    }
    if (!TryBuildPlan(out FormationPlan? plan, out string error)) {
      Status = error;
      return;
    }
    string targets = TargetList(plan);
    bool accepted = await _confirm(
        "Start Formation Flight",
        "BETA / USE AT OWN RISK. Mission Planner will continuously command these followers " +
        $"at 10 Hz relative to the selected leader:\n\n{targets}\n\n" +
        "Verify flight modes, coordinate frame, altitude reference and clear airspace. " +
        "Cancel is the default action.",
        "START FORMATION");
    if (!accepted || _disposed) {
      Status = accepted ? "Formation window is closing." : "Formation start cancelled.";
      return;
    }
    if (!TryBuildPlan(out plan, out error)) {
      Status = "Formation changed while confirmation was open. " + error;
      return;
    }

    var cancellation = new CancellationTokenSource();
    var runner = new FormationCommandRunner(_snapshot, _sink);
    IsRunning = true;
    _runCancellation = cancellation;
    Status = "Starting formation command stream…";
    Task<string> task = Task.Run(() => runner.RunAsync(
        plan, message => Dispatcher.UIThread.Post(() => {
          if (ReferenceEquals(_runCancellation, cancellation)) {
            Status = message;
          }
        }), cancellation.Token));
    _runTask = task;
    _ = ObserveRunAsync(task, cancellation);
  }

  [RelayCommand]
  private Task ArmFollowers() => ExecuteFollowerAction(
      "Arm Formation Followers", "ARM", "arm", vehicle => _sink.Arm(vehicle, true));

  [RelayCommand]
  private Task DisarmFollowers() => ExecuteFollowerAction(
      "Disarm Formation Followers", "DISARM", "disarm", vehicle => _sink.Arm(vehicle, false));

  [RelayCommand]
  private Task TakeoffFollowers() {
    double altitude = Math.Clamp(TakeoffAltitudeM, 1, 10000);
    return ExecuteFollowerAction(
        "Take Off Formation Followers", "TAKE OFF", $"enter GUIDED and take off to {altitude:0.#} m",
        vehicle => {
          _sink.SetMode(vehicle, "GUIDED");
          return _sink.Takeoff(vehicle, altitude);
        });
  }

  [RelayCommand]
  private Task LandFollowers() => ExecuteFollowerAction(
      "Land Formation Followers", "LAND", "switch to LAND", vehicle => {
        _sink.SetMode(vehicle, "Land");
        return true;
      });

  [RelayCommand]
  private Task GuidedFollowers() => ExecuteFollowerAction(
      "Set Formation Followers to GUIDED", "SET GUIDED", "switch to GUIDED", vehicle => {
        _sink.SetMode(vehicle, "GUIDED");
        return true;
      });

  [RelayCommand]
  private Task AutoFollowers() => ExecuteFollowerAction(
      "Set Formation Followers to AUTO", "SET AUTO", "switch to AUTO", vehicle => {
        _sink.SetMode(vehicle, "AUTO");
        return true;
      });

  private async Task ExecuteFollowerAction(
      string title,
      string acceptText,
      string action,
      Func<FormationVehicleSource, bool> execute) {
    if (Busy) {
      return;
    }
    FormationVehicleItem[] selected = SelectedFollowers();
    if (selected.Length == 0) {
      Status = "Select at least one follower. The leader is never included in bulk actions.";
      return;
    }
    string list = string.Join("\n", selected.Select(row => "• " + row.Label));
    bool accepted = await _confirm(title,
        $"This will {action} exactly these {selected.Length} follower(s):\n\n{list}\n\n" +
        "The leader will not be commanded. Cancel is the default action.", acceptText);
    if (!accepted || _disposed) {
      Status = accepted ? "Formation window is closing." : title + " cancelled.";
      return;
    }

    Busy = true;
    Status = title + " in progress…";
    try {
      (int success, List<string> failures) = await Task.Run(() => {
        int success = 0;
        var failures = new List<string>();
        foreach (FormationVehicleItem row in selected) {
          if (!TryResolveFresh(row.Id, out FormationVehicleSource source, out string error)) {
            failures.Add(row.Label + ": " + error);
            continue;
          }
          try {
            if (execute(source)) {
              success++;
            } else {
              failures.Add(row.Label + ": command rejected");
            }
          } catch (Exception ex) {
            failures.Add(row.Label + ": " + ex.Message);
          }
        }
        return (success, failures);
      });
      Status = failures.Count == 0
          ? $"{title}: {success} follower(s) accepted."
          : $"{title}: {success} accepted, {failures.Count} failed — " +
              string.Join("; ", failures);
    } finally {
      Busy = false;
    }
  }

  private void RefreshVehicles(bool stopRunning) {
    if (stopRunning) {
      StopBecausePlanChanged("Formation stopped because the vehicle list was refreshed.");
    }
    IReadOnlyList<FormationVehicleSource> sources = _snapshot();
    var previous = Vehicles.ToDictionary(row => row.Id);
    FormationVehicleId? previousLeader = SelectedLeader?.Id;

    _refreshing = true;
    try {
      foreach (FormationVehicleItem row in Vehicles) {
        row.Changed -= OnPlanItemChanged;
      }
      Vehicles.Clear();
      foreach (FormationVehicleSource source in sources) {
        FormationVehicleItem row;
        if (previous.TryGetValue(source.Id, out FormationVehicleItem? existing)) {
          row = existing;
          row.UpdateSource(source);
        } else {
          row = new FormationVehicleItem(source);
        }
        row.Changed += OnPlanItemChanged;
        Vehicles.Add(row);
      }
      SelectedLeader = previousLeader is { } id
          ? Vehicles.FirstOrDefault(row => row.Id == id && row.IsEligible)
          : null;
      SelectedLeader ??= Vehicles.FirstOrDefault(row => row.IsEligible);
      if (SelectedLeader != null) {
        SelectedLeader.Included = true;
      }
      UpdateRoles();
      UpdateLiveStatus();
    } finally {
      _refreshing = false;
    }
    int eligible = Vehicles.Count(row => row.IsEligible);
    Status = eligible == 0
        ? "No supported Copter/Rover autopilots were found across open MAVLink links."
        : $"Found {eligible} supported autopilot(s) across {sources.Select(source => source.Id.Link).Distinct().Count()} link(s). " +
          "Enable followers explicitly before starting.";
  }

  private void UpdateRoles() {
    foreach (FormationVehicleItem row in Vehicles) {
      row.IsLeader = ReferenceEquals(row, SelectedLeader);
    }
  }

  private void UpdateLiveStatus() {
    DateTime now = DateTime.UtcNow;
    foreach (FormationVehicleItem row in Vehicles) {
      row.UpdateLiveStatus(now);
    }
  }

  private void OnPlanItemChanged(FormationVehicleItem item) =>
      StopBecausePlanChanged("Formation stopped because a follower or offset changed.");

  private void OnConnectionsChanged() => Dispatcher.UIThread.Post(() => {
    if (!_disposed) {
      StopBecausePlanChanged("Formation stopped because MAVLink connections changed.");
      RefreshVehicles(stopRunning: false);
    }
  });

  private bool TryBuildPlan(out FormationPlan plan, out string error) {
    plan = null!;
    FormationVehicleItem? leader = SelectedLeader;
    if (leader == null || !leader.IsEligible) {
      error = "Select a live autopilot as formation leader.";
      return false;
    }
    FormationVehicleItem[] followers = SelectedFollowers();
    if (followers.Length == 0) {
      error = "Explicitly enable at least one autopilot follower.";
      return false;
    }
    if (!TryResolveFresh(leader.Id, out _, out string resolveError)) {
      error = "Leader " + resolveError;
      return false;
    }
    foreach (FormationVehicleItem follower in followers) {
      var offset = new FormationOffset(follower.X, follower.Y, follower.Z);
      if (!FormationCommandRunner.IsSafeOffset(offset)) {
        error = $"Follower {follower.Label} has a non-finite or excessive offset.";
        return false;
      }
      if (!TryResolveFresh(follower.Id, out _, out resolveError)) {
        error = "Follower " + resolveError;
        return false;
      }
    }
    plan = new FormationPlan(
        leader.Id,
        followers.Select(row => new FormationFollower(
            row.Id, new FormationOffset(row.X, row.Y, row.Z))).ToArray(),
        AlignYaw,
        AimGimbals);
    error = "";
    return true;
  }

  private FormationVehicleItem[] SelectedFollowers() =>
      [.. Vehicles.Where(row => row.Included && row.IsEligible && !row.IsLeader)];

  private bool TryResolveFresh(
      FormationVehicleId id, out FormationVehicleSource source, out string error) =>
      FormationCommandRunner.TryResolve(_snapshot(), id, DateTime.UtcNow, out source, out error);

  private string TargetList(FormationPlan plan) {
    var labels = Vehicles.ToDictionary(row => row.Id, row => row.Label);
    return string.Join("\n", plan.Followers.Select(follower =>
        $"• {labels.GetValueOrDefault(follower.Id, follower.Id.SystemId + ":" + follower.Id.ComponentId)} " +
        $"— X {follower.Offset.X:0.#} m, Y {follower.Offset.Y:0.#} m, Z {follower.Offset.Z:0.#} m"));
  }

  private async Task ObserveRunAsync(Task<string> task, CancellationTokenSource cancellation) {
    string result;
    try {
      result = await task.ConfigureAwait(false);
    } catch (Exception ex) {
      result = "Formation stopped after an internal error: " + ex.Message;
    }
    Dispatcher.UIThread.Post(() => {
      if (!ReferenceEquals(_runCancellation, cancellation)) {
        if (ReferenceEquals(_runTask, task)) {
          _runTask = null;
        }
        cancellation.Dispose();
        return;
      }
      _runCancellation = null;
      _runTask = null;
      IsRunning = false;
      Status = result;
      cancellation.Dispose();
    });
  }

  private void StopBecausePlanChanged(string reason) {
    CancellationTokenSource? cancellation = _runCancellation;
    if (cancellation == null) {
      return;
    }
    _runCancellation = null;
    IsRunning = false;
    Status = reason;
    cancellation.Cancel();
  }

  public async Task StopAsync() {
    CancellationTokenSource? cancellation = _runCancellation;
    Task<string>? task = _runTask;
    if (cancellation != null) {
      StopBecausePlanChanged("Formation stopped by operator.");
    }
    if (task == null) {
      return;
    }
    try {
      await task.WaitAsync(TimeSpan.FromSeconds(2));
    } catch (OperationCanceledException) {
    } catch (TimeoutException) {
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    AppState.Connections.Changed -= OnConnectionsChanged;
    _statusTimer.Stop();
    _runCancellation?.Cancel();
    foreach (FormationVehicleItem row in Vehicles) {
      row.Changed -= OnPlanItemChanged;
    }
  }
}

public partial class FormationVehicleItem : ObservableObject {
  private FormationVehicleSource _source;
  private bool _suppressChange;

  internal FormationVehicleItem(FormationVehicleSource source) {
    _source = source;
    _included = false;
  }

  internal event Action<FormationVehicleItem>? Changed;

  internal FormationVehicleId Id => _source.Id;

  public string Label => _source.Label;
  public string Endpoint => _source.Endpoint;
  public int SystemId => _source.Id.SystemId;
  public int ComponentId => _source.Id.ComponentId;
  public string Firmware => _source.State.cs.firmware.ToString();
  public bool IsEligible => _source.SupportsFormation;
  public string Role => IsLeader
      ? "Leader"
      : IsEligible
          ? "Follower"
          : _source.IsAutopilot
              ? $"{_source.State.cs.firmware} (disabled)"
              : "Component";

  [ObservableProperty]
  private bool _included;

  [ObservableProperty]
  private double _x;

  [ObservableProperty]
  private double _y;

  [ObservableProperty]
  private double _z;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(Role))]
  private bool _isLeader;

  [ObservableProperty]
  private string _liveStatus = "Waiting for telemetry";

  partial void OnIncludedChanged(bool value) => NotifyPlanChanged();
  partial void OnXChanged(double value) => NotifyPlanChanged();
  partial void OnYChanged(double value) => NotifyPlanChanged();
  partial void OnZChanged(double value) => NotifyPlanChanged();

  internal void SetOffset(double x, double y, double z, bool notifyChanged) {
    _suppressChange = true;
    try {
      X = x;
      Y = y;
      Z = z;
    } finally {
      _suppressChange = false;
    }
    if (notifyChanged) {
      Changed?.Invoke(this);
    }
  }

  internal void UpdateSource(FormationVehicleSource source) {
    _source = source;
    OnPropertyChanged(nameof(Label));
    OnPropertyChanged(nameof(Endpoint));
    OnPropertyChanged(nameof(Firmware));
    OnPropertyChanged(nameof(IsEligible));
    OnPropertyChanged(nameof(Role));
  }

  internal void UpdateLiveStatus(DateTime nowUtc) {
    DateTime packet = _source.State.lastvalidpacket;
    if (!_source.IsOpen) {
      LiveStatus = "Link closed";
    } else if (!_source.IsAutopilot) {
      LiveStatus = "Not an autopilot";
    } else if (!_source.SupportsFormation) {
      LiveStatus = _source.State.cs.firmware == MissionPlanner.ArduPilot.Firmwares.ArduPlane
          ? "ArduPlane attitude formation pending"
          : "Unsupported formation firmware";
    } else if (packet == DateTime.MinValue ||
               nowUtc - packet > FormationCommandRunner.MaximumTelemetryAge ||
               packet > nowUtc.AddSeconds(1)) {
      LiveStatus = "Telemetry stale";
    } else if (!FormationCommandRunner.HasPosition(_source.State)) {
      LiveStatus = $"{_source.State.cs.mode}; no position";
    } else {
      LiveStatus = $"{_source.State.cs.mode}; " +
          (_source.State.cs.armed ? "armed" : "disarmed") +
          $"; GPS {_source.State.cs.gpsstatus}";
    }
  }

  private void NotifyPlanChanged() {
    if (!_suppressChange) {
      Changed?.Invoke(this);
    }
  }
}
