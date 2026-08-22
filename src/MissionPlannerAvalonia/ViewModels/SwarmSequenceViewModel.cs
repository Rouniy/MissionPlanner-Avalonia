using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.ViewModels;

public partial class SwarmSequenceViewModel : ViewModelBase, IDisposable {
  private readonly Func<IReadOnlyList<FormationVehicleSource>> _snapshot;
  private readonly IFollowLeaderCommandSink _sink;
  private readonly Func<string, string, string, Task<bool>> _confirm;
  private bool _loading;
  private bool _disposed;
  private int _revision;
  private int _stepIndex;
  private SwarmSequenceOrigin? _origin;

  public SwarmSequenceViewModel() : this(
      () => FormationVehicleDiscovery.Snapshot(AppState.Connections),
      new MavlinkFollowLeaderCommandSink(),
      Dialogs.ConfirmDangerous) {
  }

  internal SwarmSequenceViewModel(
      Func<IReadOnlyList<FormationVehicleSource>> snapshot,
      IFollowLeaderCommandSink sink,
      Func<string, string, string, Task<bool>> confirm) {
    _snapshot = snapshot;
    _sink = sink;
    _confirm = confirm;
    AppState.Connections.Changed += OnConnectionsChanged;
    RefreshVehicles();
    UpdateStepDisplay();
  }

  public ObservableCollection<SequenceLayoutItem> Layouts { get; } = [];
  public ObservableCollection<SequenceStepItem> Steps { get; } = [];
  public ObservableCollection<SequenceVehicleOption> VehicleOptions { get; } = [];
  public ObservableCollection<SequenceAssignmentItem> Assignments { get; } = [];

  [ObservableProperty]
  private SequenceLayoutItem? _selectedLayout;

  [ObservableProperty]
  private SequenceStepItem? _selectedStep;

  [ObservableProperty]
  private SequenceVehicleOption? _selectedAnchor;

  [ObservableProperty]
  private int _droneCount = 1;

  [ObservableProperty]
  private bool _busy;

  [ObservableProperty]
  private string _stepDisplay = "Step 0 / 0";

  [ObservableProperty]
  private string _originDisplay = "Origin not captured";

  [ObservableProperty]
  private string _status =
      "Create or load a layout. Sequence files are compatible with the official JSON format.";

  partial void OnSelectedLayoutChanged(SequenceLayoutItem? value) {
    if (!_loading && value != null) {
      DroneCount = value.Offsets.Count;
    }
  }

  partial void OnSelectedStepChanged(SequenceStepItem? value) {
    if (_loading || value == null) {
      return;
    }
    SelectedLayout = Layouts.FirstOrDefault(layout =>
        string.Equals(layout.Id, value.LayoutId, StringComparison.Ordinal));
  }

  partial void OnSelectedAnchorChanged(SequenceVehicleOption? value) {
    if (!_loading) {
      ResetOrigin("Sequence origin reset because the anchor changed.");
    }
  }

  partial void OnDroneCountChanged(int value) {
    if (_loading || Layouts.Count == 0) {
      return;
    }
    int desired = Math.Clamp(value, 1, 255);
    if (desired != value) {
      DroneCount = desired;
      return;
    }
    ResizeLayouts(desired);
  }

  [RelayCommand]
  private async Task NewLayout() {
    string proposed = NextLayoutName();
    string? entered = await Dialogs.InputBox("New Sequence Layout", "Layout name", proposed);
    string name = entered?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(name)) {
      Status = "New layout cancelled.";
      return;
    }
    if (Layouts.Any(layout => string.Equals(layout.Id, name, StringComparison.Ordinal))) {
      Status = $"A layout named '{name}' already exists.";
      return;
    }
    SwarmSequenceLayout model = SelectedLayout?.ToModel().Clone(name) ?? new() { Id = name };
    if (model.Offset.Count == 0) {
      model.Offset[1] = new SwarmSequenceOffset(1, 0, 0);
    }
    var item = new SequenceLayoutItem(model);
    Subscribe(item);
    Layouts.Add(item);
    SelectedLayout = item;
    DroneCount = item.Offsets.Count;
    Changed("Created layout '" + name + "'.");
  }

  [RelayCommand]
  private void AddStep() {
    if (SelectedLayout == null) {
      Status = "Select or create a layout before adding a step.";
      return;
    }
    var step = new SequenceStepItem(SelectedLayout.Id);
    Steps.Add(step);
    SelectedStep = step;
    Changed($"Added step '{step.LayoutId}'.");
    UpdateStepDisplay();
  }

  [RelayCommand]
  private void RemoveStep() {
    int index = SelectedStep == null ? -1 : Steps.IndexOf(SelectedStep);
    if (index < 0) {
      Status = "Select a sequence step to remove.";
      return;
    }
    Steps.RemoveAt(index);
    SelectedStep = Steps.Count == 0 ? null : Steps[Math.Min(index, Steps.Count - 1)];
    _stepIndex = Math.Min(_stepIndex, Steps.Count);
    Changed("Removed sequence step.");
    UpdateStepDisplay();
  }

  [RelayCommand]
  private void MoveStepUp() => MoveSelectedStep(-1);

  [RelayCommand]
  private void MoveStepDown() => MoveSelectedStep(1);

  [RelayCommand]
  private void Refresh() {
    RefreshVehicles();
    ResetOrigin("Vehicle list refreshed; select assignments and capture a new origin.");
  }

  [RelayCommand]
  private void ResetStep() {
    _stepIndex = 0;
    SelectedStep = Steps.FirstOrDefault();
    ResetOrigin("Sequence reset to the first step; origin will be captured again.");
    ClearTargets();
    UpdateStepDisplay();
  }

  [RelayCommand]
  private async Task RunStep() {
    if (Busy) {
      return;
    }
    if (_stepIndex >= Steps.Count) {
      Status = Steps.Count == 0
          ? "Add at least one sequence step."
          : "Sequence is complete. Press Reset to run it again.";
      return;
    }
    SequenceStepItem step = Steps[_stepIndex];
    SequenceLayoutItem? layout = Layouts.FirstOrDefault(item =>
        string.Equals(item.Id, step.LayoutId, StringComparison.Ordinal));
    if (layout == null) {
      Status = $"Step {_stepIndex + 1} references missing layout '{step.LayoutId}'.";
      return;
    }
    if (!TryAssignments(layout, out SequenceVehicleOption anchor,
            out SwarmSequenceAssignment[] assignments, out string error)) {
      Status = error;
      return;
    }
    var runner = new SwarmSequenceCommandRunner(_snapshot, _sink);
    SwarmSequenceOrigin origin;
    if (_origin is { } captured) {
      origin = captured;
    } else if (!runner.TryCaptureOrigin(anchor.Id, DateTime.UtcNow, out origin, out error)) {
      Status = "Cannot capture Sequence origin: " + error;
      return;
    }
    int revision = _revision;
    int stepIndex = _stepIndex;
    SwarmSequenceLayout model = layout.ToModel();
    var plan = new SwarmSequenceCommandPlan(anchor.Id, origin, model, assignments);
    if (!runner.TryResolvePlan(plan, DateTime.UtcNow, out _, out error)) {
      Status = error;
      return;
    }
    string targets = string.Join("\n", model.Offset.OrderBy(pair => pair.Key).Select(pair =>
        $"• Sys {pair.Key}: E {pair.Value.X:0.#} m, N {pair.Value.Y:0.#} m, " +
        $"Alt {pair.Value.Z:0.#} m"));
    bool accepted = await _confirm(
        $"Run Sequence Step {stepIndex + 1}",
        "BETA / USE AT OWN RISK. This sends the official Sequence layout as relative-altitude " +
        "position targets with zero target velocity. It does not change flight mode.\n\n" +
        $"Layout: {layout.Id}\nOrigin: {origin.Latitude:0.000000}, " +
        $"{origin.Longitude:0.000000}\n\n{targets}\n\n" +
        "Verify every exact modem assignment and put aircraft in the required mode first. " +
        "Cancel is the default action.",
        "SEND SEQUENCE STEP");
    if (!accepted || _disposed) {
      Status = accepted ? "Sequence editor is closing." : "Sequence step cancelled.";
      return;
    }
    if (revision != _revision || stepIndex != _stepIndex) {
      Status = "Sequence changed while confirmation was open; no commands were sent.";
      return;
    }
    if (!TryAssignments(layout, out anchor, out assignments, out error) || anchor.Id != plan.Anchor ||
        !assignments.SequenceEqual(plan.Assignments)) {
      Status = "Vehicle assignments changed while confirmation was open; no commands were sent.";
      return;
    }

    Busy = true;
    Status = $"Sending Sequence step {stepIndex + 1}…";
    try {
      SwarmSequenceCommandResult result = await Task.Run(() =>
          runner.SendLayout(plan, DateTime.UtcNow));
      _origin = origin;
      OriginDisplay = $"Origin {origin.Latitude:0.000000}, {origin.Longitude:0.000000}";
      ClearTargets();
      foreach (SwarmSequenceCommand command in result.Commands) {
        Assignments.FirstOrDefault(item => item.SystemId == command.SystemId)?
            .SetTarget(command.Target);
      }
      _stepIndex++;
      SelectedStep = _stepIndex < Steps.Count ? Steps[_stepIndex] : null;
      Status = result.Status;
      UpdateStepDisplay();
    } catch (Exception ex) {
      Status = "Sequence step failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  [RelayCommand]
  private async Task TakeoffAssigned() {
    if (Busy) {
      return;
    }
    SequenceVehicleOption[] vehicles = Assignments
        .Select(item => item.SelectedVehicle)
        .Where(item => item != null)
        .Cast<SequenceVehicleOption>()
        .DistinctBy(item => item.Id)
        .ToArray();
    if (vehicles.Length == 0) {
      Status = "Assign at least one live Copter before takeoff.";
      return;
    }
    IReadOnlyList<FormationVehicleSource> snapshot = _snapshot();
    foreach (SequenceVehicleOption option in vehicles) {
      if (!SwarmSequenceCommandRunner.TryResolveFlightVehicle(
              snapshot, option.Id, DateTime.UtcNow, out _, out string error)) {
        Status = "Takeoff rejected: " + error;
        return;
      }
    }
    string list = string.Join("\n", vehicles.Select(item => "• " + item.Label));
    bool accepted = await _confirm(
        "Take Off Sequence Vehicles",
        "This ports the official Sequence Takeoff action: it switches the explicitly assigned " +
        "vehicles to GUIDED, arms them and requests takeoff to 2 m.\n\n" + list +
        "\n\nCancel is the default action.",
        "GUIDED, ARM AND TAKE OFF");
    if (!accepted || _disposed) {
      Status = accepted ? "Sequence editor is closing." : "Sequence takeoff cancelled.";
      return;
    }
    FormationVehicleId[] confirmedIds = vehicles.Select(item => item.Id).ToArray();
    FormationVehicleId[] currentIds = Assignments
        .Select(item => item.SelectedVehicle)
        .Where(item => item != null)
        .Cast<SequenceVehicleOption>()
        .DistinctBy(item => item.Id)
        .Select(item => item.Id)
        .ToArray();
    if (!confirmedIds.SequenceEqual(currentIds)) {
      Status = "Sequence takeoff rejected because assignments changed during confirmation.";
      return;
    }
    Busy = true;
    try {
      (int acceptedCount, List<string> failures) = await Task.Run(() => {
        int acceptedCount = 0;
        var failures = new List<string>();
        foreach (SequenceVehicleOption option in vehicles) {
          if (!SwarmSequenceCommandRunner.TryResolveFlightVehicle(
                  _snapshot(), option.Id, DateTime.UtcNow,
                  out FormationVehicleSource source, out string error)) {
            failures.Add(option.Label + ": " + error);
            continue;
          }
          try {
            _sink.SetMode(source, "GUIDED");
            bool armed = _sink.Arm(source, true);
            bool tookOff = armed && _sink.Takeoff(source, 2);
            if (tookOff) {
              acceptedCount++;
            } else {
              failures.Add(option.Label + ": arm or takeoff rejected");
            }
          } catch (Exception ex) {
            failures.Add(option.Label + ": " + ex.Message);
          }
        }
        return (acceptedCount, failures);
      });
      Status = failures.Count == 0
          ? $"Sequence takeoff accepted by {acceptedCount} vehicle(s)."
          : $"Sequence takeoff: {acceptedCount} accepted, {failures.Count} failed — " +
              string.Join("; ", failures);
    } finally {
      Busy = false;
    }
  }

  internal async Task LoadAsync(string path) {
    Busy = true;
    Status = "Loading Sequence file…";
    try {
      SwarmSequenceDocument document = await Task.Run(() => SwarmSequenceFile.Load(path));
      ApplyDocument(document);
      Status = $"Loaded {Layouts.Count} layout(s) and {Steps.Count} step(s) from {path}.";
    } catch (Exception ex) {
      Status = "Sequence load failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  internal async Task SaveAsync(string path) {
    Busy = true;
    Status = "Saving Sequence file…";
    try {
      SwarmSequenceDocument document = BuildDocument();
      await Task.Run(() => SwarmSequenceFile.Save(path, document));
      Status = $"Saved {Layouts.Count} layout(s) and {Steps.Count} step(s) to {path}.";
    } catch (Exception ex) {
      Status = "Sequence save failed: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  private void ApplyDocument(SwarmSequenceDocument document) {
    _loading = true;
    try {
      foreach (SequenceLayoutItem layout in Layouts) {
        layout.Changed -= OnLayoutChanged;
      }
      Layouts.Clear();
      Steps.Clear();
      foreach (SwarmSequenceLayout model in document.Layouts) {
        var item = new SequenceLayoutItem(model);
        Subscribe(item);
        Layouts.Add(item);
      }
      foreach (string step in document.Steps) {
        Steps.Add(new SequenceStepItem(step));
      }
      SelectedLayout = Layouts.FirstOrDefault();
      SelectedStep = Steps.FirstOrDefault();
      DroneCount = SelectedLayout?.Offsets.Count ?? 1;
      _stepIndex = 0;
      _origin = null;
      OriginDisplay = "Origin not captured";
      RebuildAssignments();
      ClearTargets();
      _revision++;
      UpdateStepDisplay();
    } finally {
      _loading = false;
    }
  }

  private SwarmSequenceDocument BuildDocument() => new() {
    Layouts = Layouts.Select(layout => layout.ToModel()).ToList(),
    Steps = Steps.Select(step => step.LayoutId).ToList(),
  };

  private bool TryAssignments(
      SequenceLayoutItem layout,
      out SequenceVehicleOption anchor,
      out SwarmSequenceAssignment[] assignments,
      out string error) {
    anchor = SelectedAnchor!;
    assignments = [];
    if (anchor == null) {
      error = "Select an exact live anchor vehicle.";
      return false;
    }
    var result = new List<SwarmSequenceAssignment>();
    foreach (SequenceOffsetItem offset in layout.Offsets.OrderBy(item => item.SystemId)) {
      SequenceAssignmentItem? assignment = Assignments.FirstOrDefault(
          item => item.SystemId == offset.SystemId);
      if (assignment?.SelectedVehicle == null) {
        error = $"Assign an exact live vehicle to layout system id {offset.SystemId}.";
        return false;
      }
      result.Add(new SwarmSequenceAssignment(offset.SystemId, assignment.SelectedVehicle.Id));
    }
    assignments = result.ToArray();
    error = "";
    return true;
  }

  private void RefreshVehicles() {
    FormationVehicleId? anchorId = SelectedAnchor?.Id;
    var previous = Assignments.ToDictionary(
        item => item.SystemId, item => item.SelectedVehicle?.Id);
    IReadOnlyList<FormationVehicleSource> sources = _snapshot();
    SequenceVehicleOption[] options = sources
        .Where(source => source.SupportsFollowLeaderFlight)
        .Select(source => new SequenceVehicleOption(source))
        .ToArray();
    _loading = true;
    try {
      VehicleOptions.Clear();
      foreach (SequenceVehicleOption option in options) {
        VehicleOptions.Add(option);
      }
      SelectedAnchor = anchorId is { } oldAnchor
          ? VehicleOptions.FirstOrDefault(item => item.Id == oldAnchor)
          : null;
      SelectedAnchor ??= VehicleOptions.FirstOrDefault();
      RebuildAssignments(previous);
    } finally {
      _loading = false;
    }
    Status = options.Length == 0
        ? "No live ArduCopter autopilots were found across open MAVLink links."
        : $"Found {options.Length} Sequence vehicle(s). Assign every layout system id to an " +
            "exact modem vehicle; duplicate sysids are never guessed.";
  }

  private void RebuildAssignments(
      IReadOnlyDictionary<int, FormationVehicleId?>? previous = null) {
    previous ??= Assignments.ToDictionary(
        item => item.SystemId, item => item.SelectedVehicle?.Id);
    int[] ids = Layouts.SelectMany(layout => layout.Offsets)
        .Select(offset => offset.SystemId).Distinct().Order().ToArray();
    foreach (SequenceAssignmentItem item in Assignments) {
      item.Changed -= OnAssignmentChanged;
    }
    Assignments.Clear();
    foreach (int systemId in ids) {
      SequenceVehicleOption? selected = null;
      if (previous.TryGetValue(systemId, out FormationVehicleId? oldId) && oldId is { } exact) {
        selected = VehicleOptions.FirstOrDefault(option => option.Id == exact);
      }
      SequenceVehicleOption[] matches = VehicleOptions
          .Where(option => option.SystemId == systemId).ToArray();
      selected ??= matches.Length == 1 ? matches[0] : null;
      var item = new SequenceAssignmentItem(systemId, VehicleOptions, selected);
      item.Changed += OnAssignmentChanged;
      Assignments.Add(item);
    }
  }

  private void ResizeLayouts(int desired) {
    int[] existing = Layouts.SelectMany(layout => layout.Offsets)
        .Select(offset => offset.SystemId).Distinct().Order().ToArray();
    if (existing.Length == desired) {
      return;
    }
    _loading = true;
    try {
      if (existing.Length < desired) {
        int next = existing.DefaultIfEmpty(0).Max() + 1;
        while (existing.Length < desired && next <= 255) {
          foreach (SequenceLayoutItem layout in Layouts) {
            layout.AddOffset(next, new SwarmSequenceOffset(next, 0, 0), notify: false);
          }
          existing = [.. existing, next];
          next++;
        }
      } else {
        foreach (int remove in existing.OrderDescending().Take(existing.Length - desired)) {
          foreach (SequenceLayoutItem layout in Layouts) {
            layout.RemoveOffset(remove, notify: false);
          }
        }
      }
    } finally {
      _loading = false;
    }
    RebuildAssignments();
    Changed($"Resized every layout to {desired} vehicle slot(s).");
  }

  private void MoveSelectedStep(int delta) {
    int index = SelectedStep == null ? -1 : Steps.IndexOf(SelectedStep);
    int destination = index + delta;
    if (index < 0 || destination < 0 || destination >= Steps.Count) {
      return;
    }
    SequenceStepItem item = Steps[index];
    Steps.Move(index, destination);
    SelectedStep = item;
    if (_stepIndex == index) {
      _stepIndex = destination;
    } else if (_stepIndex == destination) {
      _stepIndex = index;
    }
    Changed("Reordered sequence steps.");
    UpdateStepDisplay();
  }

  private void Subscribe(SequenceLayoutItem item) {
    item.Changed += OnLayoutChanged;
  }

  private void OnLayoutChanged(SequenceLayoutItem item) {
    if (_loading) {
      return;
    }
    RebuildAssignments();
    Changed($"Layout '{item.Id}' changed.");
  }

  private void OnAssignmentChanged(SequenceAssignmentItem item) {
    _revision++;
    ResetOrigin($"Sequence origin reset because system {item.SystemId} assignment changed.");
  }

  private void OnConnectionsChanged() => Dispatcher.UIThread.Post(() => {
    if (!_disposed) {
      RefreshVehicles();
      ResetOrigin("Connections changed; assignments were revalidated and origin was reset.");
    }
  });

  private void Changed(string status) {
    _revision++;
    Status = status;
  }

  private void ResetOrigin(string status) {
    _origin = null;
    OriginDisplay = "Origin not captured";
    Status = status;
  }

  private void ClearTargets() {
    foreach (SequenceAssignmentItem item in Assignments) {
      item.ClearTarget();
    }
  }

  private void UpdateStepDisplay() =>
      StepDisplay = $"Step {Math.Min(_stepIndex + 1, Steps.Count)} / {Steps.Count}";

  private string NextLayoutName() {
    int number = 1;
    while (Layouts.Any(layout => string.Equals(
               layout.Id, $"Layout {number}", StringComparison.Ordinal))) {
      number++;
    }
    return $"Layout {number}";
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    AppState.Connections.Changed -= OnConnectionsChanged;
    foreach (SequenceLayoutItem layout in Layouts) {
      layout.Changed -= OnLayoutChanged;
    }
    foreach (SequenceAssignmentItem item in Assignments) {
      item.Changed -= OnAssignmentChanged;
    }
  }
}

public sealed partial class SequenceLayoutItem : ObservableObject {
  internal SequenceLayoutItem(SwarmSequenceLayout model) {
    _id = model.Id;
    _delayStart = model.DelayStart;
    _delayEnd = model.DelayEnd;
    foreach ((int systemId, SwarmSequenceOffset offset) in model.Offset.OrderBy(pair => pair.Key)) {
      AddOffset(systemId, offset, notify: false);
    }
  }

  internal event Action<SequenceLayoutItem>? Changed;
  public ObservableCollection<SequenceOffsetItem> Offsets { get; } = [];

  [ObservableProperty]
  private string _id;

  [ObservableProperty]
  private int _delayStart;

  [ObservableProperty]
  private int _delayEnd;

  partial void OnIdChanged(string value) => Changed?.Invoke(this);
  partial void OnDelayStartChanged(int value) => Changed?.Invoke(this);
  partial void OnDelayEndChanged(int value) => Changed?.Invoke(this);

  internal void AddOffset(int systemId, SwarmSequenceOffset offset, bool notify) {
    if (Offsets.Any(item => item.SystemId == systemId)) {
      return;
    }
    var item = new SequenceOffsetItem(systemId, offset);
    item.Changed += OnOffsetChanged;
    Offsets.Add(item);
    if (notify) {
      Changed?.Invoke(this);
    }
  }

  internal void RemoveOffset(int systemId, bool notify) {
    SequenceOffsetItem? item = Offsets.FirstOrDefault(row => row.SystemId == systemId);
    if (item == null) {
      return;
    }
    item.Changed -= OnOffsetChanged;
    Offsets.Remove(item);
    if (notify) {
      Changed?.Invoke(this);
    }
  }

  internal SwarmSequenceLayout ToModel() => new() {
    Id = Id.Trim(),
    DelayStart = DelayStart,
    DelayEnd = DelayEnd,
    Offset = Offsets.ToDictionary(
        item => item.SystemId,
        item => new SwarmSequenceOffset(item.X, item.Y, item.Z)),
  };

  private void OnOffsetChanged(SequenceOffsetItem item) => Changed?.Invoke(this);
}

public sealed partial class SequenceOffsetItem : ObservableObject {
  internal SequenceOffsetItem(int systemId, SwarmSequenceOffset offset) {
    SystemId = systemId;
    _x = offset.X;
    _y = offset.Y;
    _z = offset.Z;
  }

  internal event Action<SequenceOffsetItem>? Changed;
  public int SystemId { get; }

  [ObservableProperty]
  private double _x;

  [ObservableProperty]
  private double _y;

  [ObservableProperty]
  private double _z;

  partial void OnXChanged(double value) => Changed?.Invoke(this);
  partial void OnYChanged(double value) => Changed?.Invoke(this);
  partial void OnZChanged(double value) => Changed?.Invoke(this);
}

public sealed record SequenceStepItem(string LayoutId) {
  public override string ToString() => LayoutId;
}

public sealed class SequenceVehicleOption {
  private readonly FormationVehicleSource _source;

  internal SequenceVehicleOption(FormationVehicleSource source) => _source = source;
  internal FormationVehicleId Id => _source.Id;
  public string Label => _source.Label;
  public int SystemId => _source.Id.SystemId;
  public int ComponentId => _source.Id.ComponentId;
  public string Endpoint => _source.Endpoint;
  public override string ToString() => Label;
}

public sealed partial class SequenceAssignmentItem : ObservableObject {
  internal SequenceAssignmentItem(
      int systemId,
      IEnumerable<SequenceVehicleOption> options,
      SequenceVehicleOption? selected) {
    SystemId = systemId;
    Options = options;
    _selectedVehicle = selected;
  }

  internal event Action<SequenceAssignmentItem>? Changed;
  public int SystemId { get; }
  public IEnumerable<SequenceVehicleOption> Options { get; }

  [ObservableProperty]
  private SequenceVehicleOption? _selectedVehicle;

  [ObservableProperty]
  private string _target = "—";

  partial void OnSelectedVehicleChanged(SequenceVehicleOption? value) => Changed?.Invoke(this);

  internal void SetTarget(FollowPathPoint point) => Target =
      $"{point.Latitude:0.000000}, {point.Longitude:0.000000}, {point.Altitude:0.0} m";

  internal void ClearTarget() => Target = "—";
}
