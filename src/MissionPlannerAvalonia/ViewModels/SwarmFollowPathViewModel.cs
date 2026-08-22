using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.ArduPilot;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class SwarmFollowPathViewModel : ViewModelBase, IDisposable {
  private readonly Func<IReadOnlyList<FormationVehicleSource>> _snapshot;
  private readonly IFollowPathCommandSink _sink;
  private readonly Func<string, string, string, Task<bool>> _confirm;
  private readonly DispatcherTimer _statusTimer;
  private CancellationTokenSource? _runCancellation;
  private Task<string>? _runTask;
  private bool _refreshing;
  private bool _disposed;

  public SwarmFollowPathViewModel() : this(
      () => FormationVehicleDiscovery.Snapshot(AppState.Connections),
      new MavlinkFollowPathCommandSink(),
      Dialogs.ConfirmDangerous) {
  }

  internal SwarmFollowPathViewModel(
      Func<IReadOnlyList<FormationVehicleSource>> snapshot,
      IFollowPathCommandSink sink,
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

  public ObservableCollection<FollowPathVehicleItem> Vehicles { get; } = [];

  [ObservableProperty]
  private FollowPathVehicleItem? _selectedLeader;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(RunButtonText))]
  private bool _isRunning;

  [ObservableProperty]
  private bool _busy;

  [ObservableProperty]
  private double _separationM = 2;

  [ObservableProperty]
  private double _takeoffAltitudeM = 5;

  [ObservableProperty]
  private string _status =
      "Choose a leader and explicitly enable followers in their trail order.";

  public string RunButtonText => IsRunning ? "Stop Follow Path" : "Start Follow Path";

  partial void OnSeparationMChanged(double value) =>
      StopBecausePlanChanged("Follow Path stopped because separation changed.");

  partial void OnSelectedLeaderChanged(
      FollowPathVehicleItem? oldValue, FollowPathVehicleItem? newValue) {
    if (_refreshing || newValue == null || ReferenceEquals(oldValue, newValue)) {
      UpdateRoles();
      return;
    }
    if (oldValue != null) {
      oldValue.Included = false;
      // A vehicle that stops being leader must re-enter the trail with a fresh free order.
      // Retaining its earlier hidden order can collide after several leader changes.
      oldValue.SetOrder(0, notifyChanged: false);
    }
    newValue.Included = true;
    UpdateRoles();
    AssignMissingOrders();
    ClearTargets();
    StopBecausePlanChanged("Follow Path stopped because the leader changed.");
  }

  [RelayCommand]
  private void Refresh() => RefreshVehicles(stopRunning: true);

  [RelayCommand]
  private async Task ToggleRun() {
    if (IsRunning) {
      StopBecausePlanChanged("Follow Path stopped by operator.");
      return;
    }
    if (_runTask is { IsCompleted: false }) {
      Status = "The previous Follow Path stream is still stopping; wait for it to finish.";
      return;
    }
    if (!TryBuildPlan(out FollowPathPlan? plan, out string error)) {
      Status = error;
      return;
    }

    bool accepted = await _confirm(
        "Start Swarm Follow Path",
        "BETA / USE AT OWN RISK. This is the official Mission Planner FollowPath workflow. " +
        "It will switch the explicitly selected followers to GUIDED and continuously send " +
        $"trail targets at 5 Hz:\n\n{TargetList(plan)}\n\n" +
        "Targets begin only after the leader has recorded enough trail. Verify relative-altitude " +
        "reference, ordering, separation and clear airspace. Cancel is the default action.",
        "START FOLLOW PATH");
    if (!accepted || _disposed) {
      Status = accepted ? "Follow Path window is closing." : "Follow Path start cancelled.";
      return;
    }
    if (!TryBuildPlan(out plan, out error)) {
      Status = "Follow Path changed while confirmation was open. " + error;
      return;
    }

    var cancellation = new CancellationTokenSource();
    var runner = new FollowPathCommandRunner(_snapshot, _sink);
    IsRunning = true;
    _runCancellation = cancellation;
    ClearTargets();
    Status = "Starting leader trail recording…";
    Task<string> task = Task.Run(() => runner.RunAsync(
        plan,
        result => Dispatcher.UIThread.Post(() => ApplyProgress(result, cancellation)),
        cancellation.Token));
    _runTask = task;
    _ = ObserveRunAsync(task, cancellation);
  }

  [RelayCommand]
  private Task ArmFollowers() => ExecuteFollowerAction(
      "Arm Follow Path Followers", "ARM", "arm", vehicle => _sink.Arm(vehicle, true));

  [RelayCommand]
  private Task DisarmFollowers() => ExecuteFollowerAction(
      "Disarm Follow Path Followers", "DISARM", "disarm",
      vehicle => _sink.Arm(vehicle, false), stopFollowPath: true);

  [RelayCommand]
  private Task TakeoffFollowers() {
    double altitude = Math.Clamp(TakeoffAltitudeM, 1, 10000);
    return ExecuteFollowerAction(
        "Take Off Follow Path Followers", "TAKE OFF",
        $"enter GUIDED and take off to {altitude:0.#} m",
        vehicle => {
          _sink.SetMode(vehicle, "GUIDED");
          return _sink.Takeoff(vehicle, altitude);
        });
  }

  [RelayCommand]
  private Task LandFollowers() => ExecuteFollowerAction(
      "Land Follow Path Followers", "LAND", "switch to LAND", vehicle => {
        _sink.SetMode(vehicle, "Land");
        return true;
      }, stopFollowPath: true);

  private async Task ExecuteFollowerAction(
      string title,
      string acceptText,
      string action,
      Func<FormationVehicleSource, bool> execute,
      bool stopFollowPath = false) {
    if (Busy) {
      return;
    }
    FollowPathVehicleItem[] selected = SelectedFollowers();
    if (selected.Length == 0) {
      Status = "Select at least one follower. The leader is never included in bulk actions.";
      return;
    }
    string list = string.Join("\n", selected
        .OrderBy(row => row.Order)
        .Select(row => $"• #{row.Order} {row.Label}"));
    bool accepted = await _confirm(title,
        $"This will {action} exactly these {selected.Length} follower(s):\n\n{list}\n\n" +
        "The leader will not be commanded. Cancel is the default action.", acceptText);
    if (!accepted || _disposed) {
      Status = accepted ? "Follow Path window is closing." : title + " cancelled.";
      return;
    }
    if (stopFollowPath && IsRunning) {
      StopBecausePlanChanged("Follow Path stopped before " + action + ".");
    }

    Busy = true;
    Status = title + " in progress…";
    try {
      (int success, List<string> failures) = await Task.Run(() => {
        int success = 0;
        var failures = new List<string>();
        foreach (FollowPathVehicleItem row in selected.OrderBy(row => row.Order)) {
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
      StopBecausePlanChanged("Follow Path stopped because the vehicle list was refreshed.");
    }
    IReadOnlyList<FormationVehicleSource> sources = _snapshot();
    var previous = Vehicles.ToDictionary(row => row.Id);
    FormationVehicleId? previousLeader = SelectedLeader?.Id;
    _refreshing = true;
    try {
      foreach (FollowPathVehicleItem row in Vehicles) {
        row.Changed -= OnPlanItemChanged;
      }
      Vehicles.Clear();
      foreach (FormationVehicleSource source in sources) {
        FollowPathVehicleItem row;
        if (previous.TryGetValue(source.Id, out FollowPathVehicleItem? existing)) {
          row = existing;
          row.UpdateSource(source);
        } else {
          row = new FollowPathVehicleItem(source);
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
      AssignMissingOrders();
      UpdateLiveStatus();
      ClearTargets();
    } finally {
      _refreshing = false;
    }

    int eligible = Vehicles.Count(row => row.IsEligible);
    Status = eligible == 0
        ? "No supported ArduPlane/Copter/Rover autopilots were found across open MAVLink links."
        : $"Found {eligible} Follow Path autopilot(s) across " +
            $"{sources.Select(source => source.Id.Link).Distinct().Count()} link(s). " +
            "Enable followers explicitly and assign unique positive order numbers.";
  }

  private void UpdateRoles() {
    foreach (FollowPathVehicleItem row in Vehicles) {
      row.IsLeader = ReferenceEquals(row, SelectedLeader);
    }
  }

  private void AssignMissingOrders() {
    var used = Vehicles
        .Where(row => !row.IsLeader && row.Order > 0)
        .Select(row => row.Order)
        .ToHashSet();
    int next = 1;
    foreach (FollowPathVehicleItem row in Vehicles.Where(
                 row => row.IsEligible && !row.IsLeader && row.Order <= 0)) {
      while (used.Contains(next)) {
        next++;
      }
      row.SetOrder(next, notifyChanged: false);
      used.Add(next);
    }
  }

  private void UpdateLiveStatus() {
    DateTime now = DateTime.UtcNow;
    foreach (FollowPathVehicleItem row in Vehicles) {
      row.UpdateLiveStatus(now);
    }
  }

  private void ApplyProgress(
      FollowPathTickResult result, CancellationTokenSource cancellation) {
    if (!ReferenceEquals(_runCancellation, cancellation)) {
      return;
    }
    Status = result.Status;
    ClearTargets();
    foreach (FollowPathCommand command in result.Commands) {
      FollowPathVehicleItem? row = Vehicles.FirstOrDefault(item => item.Id == command.Follower.Id);
      row?.SetTarget(command.Target, command.DistanceBehindM);
    }
  }

  private void ClearTargets() {
    foreach (FollowPathVehicleItem row in Vehicles) {
      row.ClearTarget();
    }
  }

  private void OnPlanItemChanged(FollowPathVehicleItem item) {
    ClearTargets();
    StopBecausePlanChanged("Follow Path stopped because a follower or order changed.");
  }

  private void OnConnectionsChanged() => Dispatcher.UIThread.Post(() => {
    if (!_disposed) {
      StopBecausePlanChanged("Follow Path stopped because MAVLink connections changed.");
      RefreshVehicles(stopRunning: false);
    }
  });

  private bool TryBuildPlan(out FollowPathPlan plan, out string error) {
    plan = null!;
    FollowPathVehicleItem? leader = SelectedLeader;
    if (leader == null || !leader.IsEligible) {
      error = "Select a live ArduPlane/Copter/Rover autopilot as leader.";
      return false;
    }
    FollowPathVehicleItem[] followers = SelectedFollowers();
    if (followers.Length == 0) {
      error = "Explicitly enable at least one Follow Path follower.";
      return false;
    }
    if (!TryResolveFresh(leader.Id, out _, out string resolveError)) {
      error = "Leader " + resolveError;
      return false;
    }
    if (!double.IsFinite(SeparationM) ||
        SeparationM is < FollowPathCommandRunner.MinimumSeparationM or
            > FollowPathCommandRunner.MaximumSeparationM) {
      error = $"Separation must be {FollowPathCommandRunner.MinimumSeparationM:0}–" +
          $"{FollowPathCommandRunner.MaximumSeparationM:0} m.";
      return false;
    }
    if (followers.Select(row => row.Order).Distinct().Count() != followers.Length ||
        followers.Any(row => row.Order is < 1 or > FollowPathCommandRunner.MaximumOrder)) {
      error = $"Follower order must be unique and between 1 and " +
          $"{FollowPathCommandRunner.MaximumOrder}.";
      return false;
    }
    foreach (FollowPathVehicleItem follower in followers) {
      if (!TryResolveFresh(follower.Id, out FormationVehicleSource source, out resolveError)) {
        error = "Follower " + resolveError;
        return false;
      }
      if (source.State.cs.firmware == Firmwares.ArduPlane &&
          SeparationM < FollowPathCommandRunner.MinimumPlaneSeparationM) {
        error = $"ArduPlane follower {source.Label} requires at least " +
            $"{FollowPathCommandRunner.MinimumPlaneSeparationM:0} m separation.";
        return false;
      }
    }

    plan = new FollowPathPlan(
        leader.Id,
        followers.Select(row => new FollowPathFollower(row.Id, row.Order)).ToArray(),
        SeparationM);
    error = "";
    return true;
  }

  private FollowPathVehicleItem[] SelectedFollowers() =>
      [.. Vehicles.Where(row => row.Included && row.IsEligible && !row.IsLeader)];

  private bool TryResolveFresh(
      FormationVehicleId id, out FormationVehicleSource source, out string error) =>
      FollowPathCommandRunner.TryResolveVehicle(
          _snapshot(), id, DateTime.UtcNow, out source, out error);

  private string TargetList(FollowPathPlan plan) {
    var labels = Vehicles.ToDictionary(row => row.Id, row => row.Label);
    return string.Join("\n", plan.Followers.OrderBy(follower => follower.Order).Select(follower =>
        $"• #{follower.Order} " +
        $"{labels.GetValueOrDefault(follower.Id, follower.Id.SystemId + ":" + follower.Id.ComponentId)} " +
        $"— {follower.Order * plan.SeparationM:0.#} m behind leader"));
  }

  private async Task ObserveRunAsync(Task<string> task, CancellationTokenSource cancellation) {
    string result;
    try {
      result = await task.ConfigureAwait(false);
    } catch (Exception ex) {
      result = "Follow Path stopped after an internal error: " + ex.Message;
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
      ClearTargets();
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
    ClearTargets();
    Status = reason;
    cancellation.Cancel();
  }

  public async Task StopAsync() {
    CancellationTokenSource? cancellation = _runCancellation;
    Task<string>? task = _runTask;
    if (cancellation != null) {
      StopBecausePlanChanged("Follow Path stopped by operator.");
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
    foreach (FollowPathVehicleItem row in Vehicles) {
      row.Changed -= OnPlanItemChanged;
    }
  }
}

public partial class FollowPathVehicleItem : ObservableObject {
  private FormationVehicleSource _source;
  private bool _suppressChange;

  internal FollowPathVehicleItem(FormationVehicleSource source) {
    _source = source;
  }

  internal event Action<FollowPathVehicleItem>? Changed;

  internal FormationVehicleId Id => _source.Id;

  public string Label => _source.Label;
  public string Endpoint => _source.Endpoint;
  public int SystemId => _source.Id.SystemId;
  public int ComponentId => _source.Id.ComponentId;
  public string Firmware => _source.State.cs.firmware.ToString();
  public bool IsEligible => _source.SupportsFollowPath;
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
  private int _order;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(Role))]
  private bool _isLeader;

  [ObservableProperty]
  private string _liveStatus = "Waiting for telemetry";

  [ObservableProperty]
  private string _target = "—";

  partial void OnIncludedChanged(bool value) => NotifyChanged();
  partial void OnOrderChanged(int value) => NotifyChanged();

  internal void SetOrder(int order, bool notifyChanged) {
    _suppressChange = true;
    try {
      Order = order;
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

  internal void SetTarget(FollowPathPoint point, double distanceBehindM) => Target =
      $"{distanceBehindM:0.#} m: {point.Latitude:0.000000}, " +
      $"{point.Longitude:0.000000}, {point.Altitude:0.0} m";

  internal void ClearTarget() => Target = "—";

  internal void UpdateLiveStatus(DateTime nowUtc) {
    DateTime packet = _source.State.lastvalidpacket;
    if (!_source.IsOpen) {
      LiveStatus = "Link closed";
    } else if (!_source.IsAutopilot) {
      LiveStatus = "Not an autopilot";
    } else if (!_source.SupportsFollowPath) {
      LiveStatus = "Unsupported Follow Path firmware";
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

  private void NotifyChanged() {
    if (!_suppressChange) {
      Changed?.Invoke(this);
    }
  }
}
