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

public partial class SwarmWaypointLeaderViewModel : ViewModelBase, IDisposable {
  private readonly Func<IReadOnlyList<FormationVehicleSource>> _snapshot;
  private readonly IWaypointLeaderCommandSink _sink;
  private readonly Func<string, string, string, Task<bool>> _confirm;
  private readonly DispatcherTimer _statusTimer;
  private CancellationTokenSource? _runCancellation;
  private Task<string>? _runTask;
  private WaypointLeaderCommandRunner? _runner;
  private bool _refreshing;
  private bool _disposed;

  public SwarmWaypointLeaderViewModel() : this(
      () => FormationVehicleDiscovery.Snapshot(AppState.Connections),
      new MavlinkWaypointLeaderCommandSink(),
      Dialogs.ConfirmDangerous) {
  }

  internal SwarmWaypointLeaderViewModel(
      Func<IReadOnlyList<FormationVehicleSource>> snapshot,
      IWaypointLeaderCommandSink sink,
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
  [NotifyPropertyChangedFor(nameof(CanRequestMode))]
  private bool _isRunning;

  [ObservableProperty]
  private bool _busy;

  [ObservableProperty]
  private double _separationM = 5;

  [ObservableProperty]
  private double _leadM = 20;

  [ObservableProperty]
  private double _offPathTriggerM = 10;

  [ObservableProperty]
  private double _altitudeSeparationM = 2;

  [ObservableProperty]
  private double _navigationAccelerationMps2 = 1;

  [ObservableProperty]
  private bool _vFormation;

  [ObservableProperty]
  private bool _altitudeInterleave;

  [ObservableProperty]
  private IReadOnlyList<WaypointLeaderProfilePoint> _profile = [];

  [ObservableProperty]
  private string _mode = WaypointLeaderMode.Idle.ToString();

  [ObservableProperty]
  private string _mission = "No air-master mission selected.";

  [ObservableProperty]
  private string _status =
      "Select distinct ground and air masters, then explicitly enable Copter followers.";

  public string RunButtonText => IsRunning ? "Stop Waypoint Leader" : "Start Waypoint Leader";
  public bool CanRequestMode => IsRunning;

  partial void OnSelectedGroundMasterChanged(
      WaypointLeaderVehicleItem? oldValue, WaypointLeaderVehicleItem? newValue) =>
      MastersChanged(oldValue, newValue, "ground master");

  partial void OnSelectedAirMasterChanged(
      WaypointLeaderVehicleItem? oldValue, WaypointLeaderVehicleItem? newValue) =>
      MastersChanged(oldValue, newValue, "air master");

  partial void OnSeparationMChanged(double value) => SettingsChanged("separation");
  partial void OnLeadMChanged(double value) => SettingsChanged("lead");
  partial void OnOffPathTriggerMChanged(double value) => SettingsChanged("off-path trigger");
  partial void OnAltitudeSeparationMChanged(double value) =>
      SettingsChanged("altitude separation");
  partial void OnNavigationAccelerationMps2Changed(double value) =>
      SettingsChanged("navigation acceleration");
  partial void OnVFormationChanged(bool value) => SettingsChanged("formation shape");
  partial void OnAltitudeInterleaveChanged(bool value) =>
      SettingsChanged("altitude interleave");

  [RelayCommand]
  private void Refresh() => RefreshVehicles(stopRunning: true);

  [RelayCommand]
  private async Task ToggleRun() {
    if (IsRunning) {
      StopBecausePlanChanged("Waypoint Leader stopped by operator; no further targets are sent.");
      return;
    }
    if (_runTask is { IsCompleted: false }) {
      Status = "The previous Waypoint Leader loop is still stopping; wait for it to finish.";
      return;
    }
    if (!TryBuildPlan(out WaypointLeaderPlan plan, out string error)) {
      Status = error;
      return;
    }

    bool accepted = await _confirm(
        "Start Swarm Waypoint Leader",
        "BETA / USE AT OWN RISK. This is the official Mission Planner WaypointLeader " +
        "workflow. It will write RTL_ALT and WPNAV_ACCEL/WP_ACC when those parameters exist, " +
        "switch the named air vehicles to GUIDED, arm, take off and command position targets " +
        "at 10 Hz before eventually issuing RTL. The ground master is observed only and is " +
        "never commanded.\n\nCommanded air vehicles:\n" + TargetList(plan) +
        "\n\nVerify the downloaded air-master mission, relative-altitude reference, order, " +
        "spacing and clear airspace. Cancel is the default action.",
        "START WAYPOINT LEADER");
    if (!accepted || _disposed) {
      Status = accepted ? "Waypoint Leader window is closing." : "Waypoint Leader start cancelled.";
      return;
    }
    if (!TryBuildPlan(out plan, out error)) {
      Status = "Waypoint Leader plan changed while confirmation was open. " + error;
      return;
    }

    var cancellation = new CancellationTokenSource();
    var runner = new WaypointLeaderCommandRunner(_snapshot, _sink);
    _runner = runner;
    _runCancellation = cancellation;
    IsRunning = true;
    Mode = WaypointLeaderMode.Idle.ToString();
    ClearTargets();
    Status = "Starting Waypoint Leader validation and staged takeoff…";
    Task<string> task = Task.Run(() => runner.RunAsync(
        plan,
        result => Dispatcher.UIThread.Post(() => ApplyProgress(result, cancellation)),
        cancellation.Token));
    _runTask = task;
    _ = ObserveRunAsync(task, cancellation);
  }

  [RelayCommand]
  private Task ResetMode() => RequestMode(
      WaypointLeaderMode.Idle,
      "Reset Waypoint Leader State",
      "RESET AND RESTART",
      "This resets the official state machine. On the next 10 Hz tick it can write navigation " +
      "parameters again, enter GUIDED, arm and start staged takeoff for every named air vehicle.");

  [RelayCommand]
  private Task ReturnToHome() => RequestMode(
      WaypointLeaderMode.ReturnAlongMission,
      "Return Waypoint Leader Formation",
      "RETURN ALONG MISSION",
      "This stops following the ground master and sends every named air vehicle back along the " +
      "air-master mission, followed by separated-altitude RTL.");

  [RelayCommand]
  private Task AbandonMission() => RequestMode(
      WaypointLeaderMode.LandAltitude,
      "Abandon Waypoint Leader Mission",
      "ESTABLISH ALTITUDES AND RTL",
      "This abandons path following, commands separated landing altitudes, then switches every " +
      "named air vehicle to RTL.");

  private async Task RequestMode(
      WaypointLeaderMode requested, string title, string acceptText, string warning) {
    WaypointLeaderCommandRunner? runner = _runner;
    if (!IsRunning || runner == null) {
      Status = "Waypoint Leader is not running.";
      return;
    }
    bool accepted = await _confirm(title,
        warning + "\n\nAffected air vehicles:\n" + CurrentAirVehicleList() +
        "\n\nThe ground master is not commanded. Cancel is the default action.", acceptText);
    if (!accepted || _disposed || !ReferenceEquals(_runner, runner)) {
      Status = accepted ? "Waypoint Leader run changed before the request." : title + " cancelled.";
      return;
    }
    runner.RequestMode(requested);
    Status = title + " requested.";
  }

  private void MastersChanged(
      WaypointLeaderVehicleItem? oldValue,
      WaypointLeaderVehicleItem? newValue,
      string role) {
    if (_refreshing) {
      return;
    }
    oldValue?.SetOrder(0, notifyChanged: false);
    if (newValue != null) {
      newValue.Included = false;
    }
    UpdateRoles();
    AssignMissingOrders();
    UpdateMissionProfile();
    ClearTargets();
    StopBecausePlanChanged($"Waypoint Leader stopped because the {role} changed.");
  }

  private void SettingsChanged(string setting) {
    if (!_refreshing) {
      ClearTargets();
      StopBecausePlanChanged($"Waypoint Leader stopped because {setting} changed.");
    }
  }

  private void RefreshVehicles(bool stopRunning) {
    if (stopRunning) {
      StopBecausePlanChanged("Waypoint Leader stopped because the vehicle list was refreshed.");
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
      if (SelectedAirMaster == null && SelectedGroundMaster?.IsFlightEligible == true) {
        SelectedAirMaster = SelectedGroundMaster;
      }
      UpdateRoles();
      AssignMissingOrders();
      UpdateMissionProfile();
      UpdateLiveStatus();
      ClearTargets();
    } finally {
      _refreshing = false;
    }

    int flight = Vehicles.Count(row => row.IsFlightEligible);
    Status = flight == 0
        ? "No live ArduCopter autopilots were found across open MAVLink links."
        : $"Found {flight} Waypoint Leader Copter(s) across " +
            $"{sources.Select(source => source.Id.Link).Distinct().Count()} link(s). " +
            "Select distinct masters and explicitly enable any additional followers.";
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

  private void UpdateMissionProfile() {
    if (SelectedAirMaster == null) {
      Profile = [];
      Mission = "No air-master mission selected.";
      return;
    }
    if (!WaypointLeaderMissionPath.TryBuild(
            SelectedAirMaster.Source.State, out WaypointLeaderMissionPath path, out string error)) {
      Profile = [];
      Mission = error;
      return;
    }
    Profile = path.Profile;
    Mission = $"Air-master mission: {path.Profile.Count} vertex/vertices, " +
        $"{path.LengthM:0.0} m, signature {path.Signature[..8]}.";
  }

  private void UpdateLiveStatus() {
    DateTime now = DateTime.UtcNow;
    // Upstream refreshes its ZedGraph whenever the air-master mission changes. Do the same so a
    // mission downloaded after opening this window appears without closing/reopening the tool.
    UpdateMissionProfile();
    WaypointLeaderMissionPath? path = null;
    if (SelectedAirMaster != null) {
      WaypointLeaderMissionPath.TryBuild(
          SelectedAirMaster.Source.State, out path!, out _);
    }
    foreach (WaypointLeaderVehicleItem row in Vehicles) {
      row.UpdateLiveStatus(now, path);
    }
  }

  private void ApplyProgress(
      WaypointLeaderTickResult result, CancellationTokenSource cancellation) {
    if (!ReferenceEquals(_runCancellation, cancellation)) {
      return;
    }
    Mode = result.Mode.ToString();
    Status = result.Status;
    ClearTargets();
    foreach (WaypointLeaderCommand command in result.Commands) {
      Vehicles.FirstOrDefault(row => row.Id == command.Vehicle.Id)?.SetTarget(command.Target);
    }
  }

  private void OnPlanItemChanged(WaypointLeaderVehicleItem item) {
    if (item.IsGroundMaster || item.IsAirMaster) {
      item.Included = false;
    }
    ClearTargets();
    StopBecausePlanChanged("Waypoint Leader stopped because a follower or order changed.");
  }

  private void OnConnectionsChanged() => Dispatcher.UIThread.Post(() => {
    if (!_disposed) {
      StopBecausePlanChanged("Waypoint Leader stopped because MAVLink connections changed.");
      RefreshVehicles(stopRunning: false);
    }
  });

  internal bool TryBuildPlan(out WaypointLeaderPlan plan, out string error) {
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
    if (ground.Id == air.Id) {
      error = "Ground master and air master must be different vehicles.";
      return false;
    }
    var settings = new WaypointLeaderSettings(
        SeparationM, LeadM, OffPathTriggerM, AltitudeSeparationM,
        NavigationAccelerationMps2, VFormation, AltitudeInterleave);
    if (!WaypointLeaderCommandRunner.ValidateSettings(settings, out error)) {
      return false;
    }
    if (!WaypointLeaderMissionPath.TryBuild(
            air.Source.State, out WaypointLeaderMissionPath path, out error)) {
      return false;
    }
    WaypointLeaderVehicleItem[] followers = SelectedFollowers();
    if (followers.Select(row => row.Order).Distinct().Count() != followers.Length ||
        followers.Any(row => row.Order is < 1 or > WaypointLeaderCommandRunner.MaximumOrder)) {
      error = $"Follower order must be unique and between 1 and " +
          $"{WaypointLeaderCommandRunner.MaximumOrder}.";
      return false;
    }

    plan = new WaypointLeaderPlan(
        ground.Id,
        air.Id,
        followers.OrderBy(row => row.Order)
            .Select(row => new WaypointLeaderFollower(row.Id, row.Order)).ToArray(),
        settings,
        path.Signature);
    var validator = new WaypointLeaderCommandRunner(_snapshot, _sink);
    if (!validator.TryResolvePlan(plan, DateTime.UtcNow,
            out _, out _, out _, out _, out error)) {
      plan = null!;
      return false;
    }
    error = "";
    return true;
  }

  private WaypointLeaderVehicleItem[] SelectedFollowers() =>
      [.. Vehicles.Where(row => row.Included && row.IsFollower)];

  private string TargetList(WaypointLeaderPlan plan) {
    var labels = Vehicles.ToDictionary(row => row.Id, row => row.Label);
    var rows = new List<string> {
      "• Air master " + labels.GetValueOrDefault(plan.AirMaster,
          plan.AirMaster.SystemId + ":" + plan.AirMaster.ComponentId),
    };
    rows.AddRange(plan.Followers.OrderBy(item => item.Order).Select(item =>
        $"• Follower #{item.Order} " + labels.GetValueOrDefault(
            item.Id, item.Id.SystemId + ":" + item.Id.ComponentId)));
    return string.Join("\n", rows);
  }

  private string CurrentAirVehicleList() {
    var rows = new List<string>();
    if (SelectedAirMaster != null) {
      rows.Add("• Air master " + SelectedAirMaster.Label);
    }
    rows.AddRange(SelectedFollowers().OrderBy(row => row.Order)
        .Select(row => $"• Follower #{row.Order} {row.Label}"));
    return rows.Count == 0 ? "• none" : string.Join("\n", rows);
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
      result = "Waypoint Leader stopped after an internal error: " + ex.Message;
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
      _runner = null;
      IsRunning = false;
      Mode = WaypointLeaderMode.Idle.ToString();
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
    _runner = null;
    IsRunning = false;
    Mode = WaypointLeaderMode.Idle.ToString();
    ClearTargets();
    Status = reason;
    cancellation.Cancel();
  }

  public async Task StopAsync() {
    CancellationTokenSource? cancellation = _runCancellation;
    Task<string>? task = _runTask;
    if (cancellation != null) {
      StopBecausePlanChanged("Waypoint Leader stopped by operator; no further targets are sent.");
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

public partial class WaypointLeaderVehicleItem : ObservableObject {
  private FormationVehicleSource _source;
  private bool _suppressChange;

  internal WaypointLeaderVehicleItem(FormationVehicleSource source) => _source = source;

  internal event Action<WaypointLeaderVehicleItem>? Changed;
  internal FormationVehicleSource Source => _source;
  internal FormationVehicleId Id => _source.Id;

  public string Label => _source.Label;
  public string Endpoint => _source.Endpoint;
  public int SystemId => _source.Id.SystemId;
  public int ComponentId => _source.Id.ComponentId;
  public string Firmware => _source.State.cs.firmware.ToString();
  public bool IsGroundEligible => _source.IsAutopilot;
  public bool IsFlightEligible => _source.SupportsWaypointLeaderFlight;
  public bool IsFollower => IsFlightEligible && !IsGroundMaster && !IsAirMaster;
  public double CurrentAltitudeM => _source.State.cs.alt;
  public string Role => IsGroundMaster && IsAirMaster
      ? "Invalid: both masters"
      : IsGroundMaster
          ? "Ground master"
          : IsAirMaster
              ? "Air master"
              : IsFollower
                  ? "Follower candidate"
                  : _source.IsAutopilot
                      ? $"{Firmware} (not flight-capable)"
                      : "Component";

  [ObservableProperty]
  private bool _included;

  [ObservableProperty]
  private int _order;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(Role))]
  [NotifyPropertyChangedFor(nameof(IsFollower))]
  private bool _isGroundMaster;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(Role))]
  [NotifyPropertyChangedFor(nameof(IsFollower))]
  private bool _isAirMaster;

  [ObservableProperty]
  private string _liveStatus = "Waiting for telemetry";

  [ObservableProperty]
  private string _pathPosition = "—";

  [ObservableProperty]
  private string _target = "—";

  [ObservableProperty]
  private double _pathDistanceM = double.NaN;

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
    OnPropertyChanged(nameof(IsGroundEligible));
    OnPropertyChanged(nameof(IsFlightEligible));
    OnPropertyChanged(nameof(IsFollower));
    OnPropertyChanged(nameof(Role));
    OnPropertyChanged(nameof(CurrentAltitudeM));
  }

  internal void SetTarget(FollowPathPoint point) => Target =
      $"{point.Latitude:0.000000}, {point.Longitude:0.000000}, {point.Altitude:0.0} m";

  internal void ClearTarget() => Target = "—";

  internal void UpdateLiveStatus(DateTime nowUtc, WaypointLeaderMissionPath? path) {
    DateTime packet = _source.State.lastvalidpacket;
    if (!_source.IsOpen) {
      LiveStatus = "Link closed";
    } else if (!_source.IsAutopilot) {
      LiveStatus = "Not an autopilot";
    } else if (IsAirMaster || IsFollower ? !IsFlightEligible : false) {
      LiveStatus = "Official workflow requires ArduCopter";
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
    OnPropertyChanged(nameof(CurrentAltitudeM));

    if (path != null && FormationCommandRunner.HasPosition(_source.State) &&
        path.TryClosest(new FollowPathPoint(
            _source.State.cs.lat, _source.State.cs.lng, _source.State.cs.alt),
            out double along, out double away)) {
      PathDistanceM = along;
      PathPosition = $"{along:0.0} m; off {away:0.0} m";
    } else {
      PathDistanceM = double.NaN;
      PathPosition = "—";
    }
  }

  private void NotifyChanged() {
    if (!_suppressChange) {
      Changed?.Invoke(this);
    }
  }
}
