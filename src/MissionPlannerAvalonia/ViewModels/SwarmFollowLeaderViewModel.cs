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

public partial class SwarmFollowLeaderViewModel : ViewModelBase, IDisposable {
  private readonly Func<IReadOnlyList<FormationVehicleSource>> _snapshot;
  private readonly IFollowLeaderCommandSink _sink;
  private readonly Func<string, string, string, Task<bool>> _confirm;
  private readonly DispatcherTimer _statusTimer;
  private CancellationTokenSource? _runCancellation;
  private Task<string>? _runTask;
  private bool _refreshing;
  private bool _disposed;

  public SwarmFollowLeaderViewModel() : this(
      () => FormationVehicleDiscovery.Snapshot(AppState.Connections),
      new MavlinkFollowLeaderCommandSink(),
      Dialogs.ConfirmDangerous) {
  }

  internal SwarmFollowLeaderViewModel(
      Func<IReadOnlyList<FormationVehicleSource>> snapshot,
      IFollowLeaderCommandSink sink,
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

  public ObservableCollection<WaypointLeaderVehicleItem> Vehicles { get; } = [];

  [ObservableProperty]
  private WaypointLeaderVehicleItem? _selectedGroundMaster;

  [ObservableProperty]
  private WaypointLeaderVehicleItem? _selectedAirMaster;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(RunButtonText))]
  private bool _isRunning;

  [ObservableProperty]
  private bool _busy;

  [ObservableProperty]
  private double _separationM = 5;

  [ObservableProperty]
  private double _leadM = 20;

  [ObservableProperty]
  private double _altitudeM = 10;

  [ObservableProperty]
  private double _takeoffAltitudeM = 5;

  [ObservableProperty]
  private string _status =
      "Select distinct ground and air masters, then explicitly enable ordered Copter followers.";

  public string RunButtonText => IsRunning ? "Stop Follow Leader" : "Start Follow Leader";

  partial void OnSelectedGroundMasterChanged(
      WaypointLeaderVehicleItem? oldValue, WaypointLeaderVehicleItem? newValue) =>
      MasterChanged(newValue, "ground master");

  partial void OnSelectedAirMasterChanged(
      WaypointLeaderVehicleItem? oldValue, WaypointLeaderVehicleItem? newValue) =>
      MasterChanged(newValue, "air master");

  partial void OnSeparationMChanged(double value) => SettingChanged("separation");
  partial void OnLeadMChanged(double value) => SettingChanged("lead");
  partial void OnAltitudeMChanged(double value) => SettingChanged("altitude");

  [RelayCommand]
  private void Refresh() => RefreshVehicles(stopRunning: true);

  [RelayCommand]
  private async Task ToggleRun() {
    if (IsRunning) {
      StopBecausePlanChanged("Follow Leader stopped by operator; no further targets are sent.");
      return;
    }
    if (_runTask is { IsCompleted: false }) {
      Status = "The previous Follow Leader loop is still stopping; wait for it to finish.";
      return;
    }
    if (!TryBuildPlan(out FollowLeaderPlan plan, out string error)) {
      Status = error;
      return;
    }

    bool accepted = await _confirm(
        "Start Swarm Follow Leader",
        "BETA / USE AT OWN RISK. This ports the official Mission Planner FollowLeader " +
        "controller. The ground master is observed only. Position/velocity targets are sent " +
        "to the named air master and followers at 10 Hz. The air master flies Separation metres " +
        "ahead on the ground course; ordered followers occupy the recorded ground trail.\n\n" +
        "Commanded aircraft:\n" + AirVehicleList(plan) +
        "\n\nVerify GUIDED capability, relative-altitude reference, ordering, clear airspace " +
        "and the ground mission. Cancel is the default action.",
        "START FOLLOW LEADER");
    if (!accepted || _disposed) {
      Status = accepted ? "Follow Leader window is closing." : "Follow Leader start cancelled.";
      return;
    }
    if (!TryBuildPlan(out plan, out error)) {
      Status = "Follow Leader plan changed while confirmation was open. " + error;
      return;
    }

    var cancellation = new CancellationTokenSource();
    var runner = new FollowLeaderCommandRunner(_snapshot, _sink);
    _runCancellation = cancellation;
    IsRunning = true;
    ClearTargets();
    Status = "Starting Follow Leader command stream…";
    Task<string> task = Task.Run(() => runner.RunAsync(
        plan,
        result => Dispatcher.UIThread.Post(() => ApplyProgress(result, cancellation)),
        cancellation.Token));
    _runTask = task;
    _ = ObserveRunAsync(task, cancellation);
  }

  [RelayCommand]
  private Task ArmAirGroup() => ExecuteAction(
      "Arm Follow Leader Air Group", "ARM AIR GROUP", "arm", includeGround: false,
      vehicle => _sink.Arm(vehicle, true));

  [RelayCommand]
  private Task DisarmAirGroup() => ExecuteAction(
      "Disarm Follow Leader Air Group", "DISARM AIR GROUP", "disarm", includeGround: false,
      vehicle => _sink.Arm(vehicle, false), stopRunning: true);

  [RelayCommand]
  private Task TakeoffAirGroup() {
    double altitude = Math.Clamp(TakeoffAltitudeM, 1, 10000);
    return ExecuteAction(
        "Take Off Follow Leader Air Group", "TAKE OFF AIR GROUP",
        $"enter GUIDED and take off to {altitude:0.#} m", includeGround: false,
        vehicle => {
          _sink.SetMode(vehicle, "GUIDED");
          return _sink.Takeoff(vehicle, altitude);
        });
  }

  [RelayCommand]
  private Task GuidedAirGroup() => ExecuteAction(
      "Set Follow Leader Air Group to GUIDED", "SET GUIDED", "switch to GUIDED",
      includeGround: false,
      vehicle => {
        _sink.SetMode(vehicle, "GUIDED");
        return true;
      });

  [RelayCommand]
  private Task NavGuidedAirGroup() => ExecuteAction(
      "Enable Follow Leader NAV GUIDED", "ENABLE NAV GUIDED", "enable NAV GUIDED",
      includeGround: false, _sink.EnableNavGuided);

  [RelayCommand]
  private Task AutoAllRoles() => ExecuteAction(
      "Set Follow Leader Group to AUTO", "SET ALL ROLES AUTO", "switch to AUTO",
      includeGround: true,
      vehicle => {
        _sink.SetMode(vehicle, "AUTO");
        return true;
      }, stopRunning: true);

  private async Task ExecuteAction(
      string title,
      string acceptText,
      string action,
      bool includeGround,
      Func<FormationVehicleSource, bool> execute,
      bool stopRunning = false) {
    if (Busy) {
      return;
    }
    if (!TryBuildPlan(out FollowLeaderPlan plan, out string error)) {
      Status = error;
      return;
    }
    FormationVehicleId[] ids = includeGround
        ? [plan.GroundMaster, plan.AirMaster, .. plan.Followers.OrderBy(x => x.Order).Select(x => x.Id)]
        : [plan.AirMaster, .. plan.Followers.OrderBy(x => x.Order).Select(x => x.Id)];
    var labels = Vehicles.ToDictionary(row => row.Id, row => row.Label);
    string list = string.Join("\n", ids.Select(id => "• " +
        labels.GetValueOrDefault(id, id.SystemId + ":" + id.ComponentId)));
    bool accepted = await _confirm(title,
        $"This will {action} exactly these {ids.Length} vehicle(s):\n\n{list}\n\n" +
        (includeGround ? "The ground master is included." : "The ground master is not commanded.") +
        " Cancel is the default action.", acceptText);
    if (!accepted || _disposed) {
      Status = accepted ? "Follow Leader window is closing." : title + " cancelled.";
      return;
    }
    if (!TryBuildPlan(out FollowLeaderPlan currentPlan, out error)) {
      Status = title + " rejected because the group changed: " + error;
      return;
    }
    FormationVehicleId[] currentIds = includeGround
        ? [currentPlan.GroundMaster, currentPlan.AirMaster,
            .. currentPlan.Followers.OrderBy(x => x.Order).Select(x => x.Id)]
        : [currentPlan.AirMaster,
            .. currentPlan.Followers.OrderBy(x => x.Order).Select(x => x.Id)];
    if (!ids.SequenceEqual(currentIds)) {
      Status = title + " rejected because roles or follower order changed during confirmation.";
      return;
    }
    if (stopRunning && IsRunning) {
      StopBecausePlanChanged("Follow Leader stopped before " + action + ".");
    }

    Busy = true;
    Status = title + " in progress…";
    try {
      (int success, List<string> failures) = await Task.Run(() => {
        int success = 0;
        var failures = new List<string>();
        foreach (FormationVehicleId id in ids) {
          if (!FormationCommandRunner.TryResolveAutopilot(
                  _snapshot(), id, DateTime.UtcNow,
                  out FormationVehicleSource source, out string resolveError)) {
            failures.Add(labels.GetValueOrDefault(id, id.SystemId + ":" + id.ComponentId) +
                ": " + resolveError);
            continue;
          }
          try {
            if (execute(source)) {
              success++;
            } else {
              failures.Add(source.Label + ": command rejected");
            }
          } catch (Exception ex) {
            failures.Add(source.Label + ": " + ex.Message);
          }
        }
        return (success, failures);
      });
      Status = failures.Count == 0
          ? $"{title}: {success} vehicle(s) accepted."
          : $"{title}: {success} accepted, {failures.Count} failed — " +
              string.Join("; ", failures);
    } finally {
      Busy = false;
    }
  }

  private void MasterChanged(WaypointLeaderVehicleItem? newValue, string role) {
    if (_refreshing) {
      return;
    }
    if (newValue != null) {
      newValue.Included = false;
      newValue.SetOrder(0, notifyChanged: false);
    }
    UpdateRoles();
    AssignMissingOrders();
    ClearTargets();
    StopBecausePlanChanged($"Follow Leader stopped because the {role} changed.");
  }

  private void SettingChanged(string setting) {
    if (!_refreshing) {
      ClearTargets();
      StopBecausePlanChanged($"Follow Leader stopped because {setting} changed.");
    }
  }

  private void RefreshVehicles(bool stopRunning) {
    if (stopRunning) {
      StopBecausePlanChanged("Follow Leader stopped because the vehicle list was refreshed.");
    }
    IReadOnlyList<FormationVehicleSource> sources = _snapshot();
    var previous = Vehicles.ToDictionary(row => row.Id);
    FormationVehicleId? groundId = SelectedGroundMaster?.Id;
    FormationVehicleId? airId = SelectedAirMaster?.Id;
    _refreshing = true;
    try {
      foreach (WaypointLeaderVehicleItem row in Vehicles) {
        row.Changed -= OnPlanItemChanged;
      }
      Vehicles.Clear();
      foreach (FormationVehicleSource source in sources) {
        WaypointLeaderVehicleItem row;
        if (previous.TryGetValue(source.Id, out WaypointLeaderVehicleItem? existing)) {
          row = existing;
          row.UpdateSource(source);
        } else {
          row = new WaypointLeaderVehicleItem(source);
        }
        row.Changed += OnPlanItemChanged;
        Vehicles.Add(row);
      }
      SelectedGroundMaster = groundId is { } oldGround
          ? Vehicles.FirstOrDefault(row => row.Id == oldGround && row.IsGroundEligible)
          : null;
      SelectedAirMaster = airId is { } oldAir
          ? Vehicles.FirstOrDefault(row => row.Id == oldAir && row.IsFlightEligible)
          : null;
      SelectedGroundMaster ??= Vehicles.FirstOrDefault(row => row.IsGroundEligible);
      SelectedAirMaster ??= Vehicles.FirstOrDefault(row =>
          row.IsFlightEligible && !ReferenceEquals(row, SelectedGroundMaster));
      UpdateRoles();
      AssignMissingOrders();
      UpdateLiveStatus();
      ClearTargets();
    } finally {
      _refreshing = false;
    }
    int flight = Vehicles.Count(row => row.IsFlightEligible);
    Status = flight == 0
        ? "No live ArduCopter autopilots were found across open MAVLink links."
        : $"Found {flight} Follow Leader Copter(s) across " +
            $"{sources.Select(source => source.Id.Link).Distinct().Count()} link(s). " +
            "Select distinct masters and explicitly enable ordered followers.";
  }

  private void UpdateRoles() {
    foreach (WaypointLeaderVehicleItem row in Vehicles) {
      row.IsGroundMaster = ReferenceEquals(row, SelectedGroundMaster);
      row.IsAirMaster = ReferenceEquals(row, SelectedAirMaster);
      if (row.IsGroundMaster || row.IsAirMaster) {
        row.Included = false;
      }
    }
  }

  private void AssignMissingOrders() {
    var used = Vehicles.Where(row => row.IsFollower && row.Order > 0)
        .Select(row => row.Order).ToHashSet();
    int next = 1;
    foreach (WaypointLeaderVehicleItem row in Vehicles.Where(
                 row => row.IsFlightEligible && !row.IsGroundMaster && !row.IsAirMaster &&
                     row.Order <= 0)) {
      while (used.Contains(next)) {
        next++;
      }
      row.SetOrder(next, notifyChanged: false);
      used.Add(next);
    }
  }

  private void UpdateLiveStatus() {
    DateTime now = DateTime.UtcNow;
    foreach (WaypointLeaderVehicleItem row in Vehicles) {
      row.UpdateLiveStatus(now, path: null);
    }
  }

  private void ApplyProgress(
      FollowLeaderTickResult result, CancellationTokenSource cancellation) {
    if (!ReferenceEquals(_runCancellation, cancellation)) {
      return;
    }
    Status = result.Status;
    ClearTargets();
    foreach (FollowLeaderCommand command in result.Commands) {
      Vehicles.FirstOrDefault(row => row.Id == command.Vehicle.Id)?.SetTarget(command.Target);
    }
  }

  private void OnPlanItemChanged(WaypointLeaderVehicleItem item) {
    if (item.IsGroundMaster || item.IsAirMaster) {
      item.Included = false;
    }
    ClearTargets();
    StopBecausePlanChanged("Follow Leader stopped because a follower or order changed.");
  }

  private void OnConnectionsChanged() => Dispatcher.UIThread.Post(() => {
    if (!_disposed) {
      StopBecausePlanChanged("Follow Leader stopped because MAVLink connections changed.");
      RefreshVehicles(stopRunning: false);
    }
  });

  internal bool TryBuildPlan(out FollowLeaderPlan plan, out string error) {
    plan = null!;
    WaypointLeaderVehicleItem? ground = SelectedGroundMaster;
    WaypointLeaderVehicleItem? air = SelectedAirMaster;
    if (ground == null || !ground.IsGroundEligible) {
      error = "Select a live autopilot as ground master.";
      return false;
    }
    if (air == null || !air.IsFlightEligible) {
      error = "Select a live ArduCopter autopilot as air master.";
      return false;
    }
    var followers = Vehicles
        .Where(row => row.Included && row.IsFollower)
        .OrderBy(row => row.Order)
        .ToArray();
    plan = new FollowLeaderPlan(
        ground.Id,
        air.Id,
        followers.Select(row => new FollowLeaderFollower(row.Id, row.Order)).ToArray(),
        new FollowLeaderSettings(SeparationM, LeadM, AltitudeM));
    var validator = new FollowLeaderCommandRunner(_snapshot, _sink);
    if (!validator.TryResolvePlan(plan, DateTime.UtcNow,
            out _, out _, out _, out error)) {
      plan = null!;
      return false;
    }
    error = "";
    return true;
  }

  private string AirVehicleList(FollowLeaderPlan plan) {
    var labels = Vehicles.ToDictionary(row => row.Id, row => row.Label);
    var rows = new List<string> {
      "• Air master " + labels.GetValueOrDefault(
          plan.AirMaster, plan.AirMaster.SystemId + ":" + plan.AirMaster.ComponentId),
    };
    rows.AddRange(plan.Followers.OrderBy(item => item.Order).Select(item =>
        $"• Follower #{item.Order} " + labels.GetValueOrDefault(
            item.Id, item.Id.SystemId + ":" + item.Id.ComponentId)));
    return string.Join("\n", rows);
  }

  private void ClearTargets() {
    foreach (WaypointLeaderVehicleItem row in Vehicles) {
      row.ClearTarget();
    }
  }

  private async Task ObserveRunAsync(Task<string> task, CancellationTokenSource cancellation) {
    string result;
    try {
      result = await task.ConfigureAwait(false);
    } catch (Exception ex) {
      result = "Follow Leader stopped after an internal error: " + ex.Message;
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
      StopBecausePlanChanged("Follow Leader stopped by operator; no further targets are sent.");
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
    foreach (WaypointLeaderVehicleItem row in Vehicles) {
      row.Changed -= OnPlanItemChanged;
    }
  }
}
