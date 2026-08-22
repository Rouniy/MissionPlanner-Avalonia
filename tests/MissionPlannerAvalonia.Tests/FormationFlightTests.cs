using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public sealed class FormationFlightTests {
  [Theory]
  [InlineData(0, 10, 0)]
  [InlineData(0, 0, 10)]
  [InlineData(90, 10, 0)]
  [InlineData(90, 0, 10)]
  [InlineData(237, -18.5, 42.25)]
  public void TargetAndInversePreserveLeaderRelativeOffset(
      double yaw, double x, double y) {
    FormationTarget target = FormationGeometry.TargetFromLeader(
        35.123456, 33.654321, 120, yaw, 1.25, -2.5, 0.3,
        new FormationOffset(x, y, 7.5));

    FormationOffset inverse = FormationGeometry.OffsetFromLeader(
        35.123456, 33.654321, 120, yaw,
        target.Latitude, target.Longitude, target.Altitude);

    Assert.Equal(x, inverse.X, 4);
    Assert.Equal(y, inverse.Y, 4);
    Assert.Equal(7.5, inverse.Z, 6);
    Assert.Equal(1.25, target.VelocityNorth);
    Assert.Equal(-2.5, target.VelocityEast);
    Assert.Equal(0.3, target.VelocityDown);
    Assert.Equal(yaw, target.YawDegrees);
  }

  [Fact]
  public void TickSendsOnlyPlannedFollowerOnItsOriginalLink() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 11, 1, "leader", now,
        35, 33, 100, yaw: 90, vx: 3, vy: 4, vz: -0.5);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 22, 1, "follower", now,
        35.0001, 33.0001, 95);
    FormationVehicleSource unselected = Source(new MAVLinkInterface(), 33, 1, "other", now,
        35.0002, 33.0002, 90);
    IReadOnlyList<FormationVehicleSource> snapshot = [leader, follower, unselected];
    var sink = new RecordingSink();
    var runner = new FormationCommandRunner(() => snapshot, sink);
    var plan = new FormationPlan(leader.Id,
        [new FormationFollower(follower.Id, new FormationOffset(10, -5, 6))],
        AlignYaw: true, AimGimbals: false);

    FormationTickResult result = runner.Tick(plan, now);

    Assert.True(result.Continue);
    FormationSetpointCall call = Assert.Single(sink.Setpoints);
    Assert.Equal(follower.Id, call.Vehicle.Id);
    Assert.NotEqual(unselected.Id, call.Vehicle.Id);
    Assert.True(call.AlignYaw);
    Assert.False(call.AimGimbal);
    Assert.Equal(106, call.Target.Altitude, 5);
    Assert.Equal(3, call.Target.VelocityNorth);
    Assert.Equal(4, call.Target.VelocityEast);
    Assert.Equal(-0.5, call.Target.VelocityDown);
  }

  [Fact]
  public void TickStopsBeforeSendingWhenFollowerDisappears() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        35, 33, 100);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 2, 1, "follower", now,
        35.0001, 33.0001, 100);
    IReadOnlyList<FormationVehicleSource> snapshot = [leader, follower];
    var sink = new RecordingSink();
    var runner = new FormationCommandRunner(() => snapshot, sink);
    var plan = new FormationPlan(leader.Id,
        [new FormationFollower(follower.Id, new FormationOffset(10, 0, 0))], false, false);
    snapshot = [leader];

    FormationTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("disappeared", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Setpoints);
  }

  [Fact]
  public void SameSystemOnReplacementLinkDoesNotMatchCapturedFollower() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        35, 33, 100);
    FormationVehicleSource original = Source(new MAVLinkInterface(), 2, 1, "udp-a", now,
        35.0001, 33.0001, 100);
    FormationVehicleSource replacement = Source(new MAVLinkInterface(), 2, 1, "udp-b", now,
        35.0001, 33.0001, 100);
    IReadOnlyList<FormationVehicleSource> snapshot = [leader, replacement];
    var sink = new RecordingSink();
    var runner = new FormationCommandRunner(() => snapshot, sink);
    var plan = new FormationPlan(leader.Id,
        [new FormationFollower(original.Id, new FormationOffset(10, 0, 0))], false, false);

    FormationTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("another link", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Setpoints);
  }

  [Theory]
  [InlineData(true, 1)]
  [InlineData(false, 42)]
  public void StaleOrNonAutopilotTargetIsRejected(bool stale, byte componentId) {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        35, 33, 100);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 2, componentId, "follower",
        stale ? now.AddMinutes(-1) : now, 35.0001, 33.0001, 100);
    var sink = new RecordingSink();
    var runner = new FormationCommandRunner(() => [leader, follower], sink);
    var plan = new FormationPlan(leader.Id,
        [new FormationFollower(follower.Id, new FormationOffset(5, 0, 0))], false, false);

    FormationTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Empty(sink.Setpoints);
    Assert.Contains(stale ? "stale" : "not an autopilot", result.Status,
        StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void TickValidatesEveryFollowerBeforeSendingAnySetpoint() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        35, 33, 100);
    FormationVehicleSource healthy = Source(new MAVLinkInterface(), 2, 1, "healthy", now,
        35.0001, 33.0001, 100);
    FormationVehicleSource stale = Source(new MAVLinkInterface(), 3, 1, "stale",
        now.AddMinutes(-1), 35.0002, 33.0002, 100);
    var sink = new RecordingSink();
    var runner = new FormationCommandRunner(() => [leader, healthy, stale], sink);
    var plan = new FormationPlan(leader.Id,
        [
          new FormationFollower(healthy.Id, new FormationOffset(5, 0, 0)),
          new FormationFollower(stale.Id, new FormationOffset(-5, 0, 0)),
        ], false, false);

    FormationTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Empty(sink.Setpoints);
  }

  [Theory]
  [InlineData(double.NaN, 0, 0)]
  [InlineData(double.PositiveInfinity, 0, 0)]
  [InlineData(100001, 0, 0)]
  [InlineData(0, 0, 10001)]
  public void InvalidOffsetStopsBeforeAnySetpoint(double x, double y, double z) {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        35, 33, 100);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 2, 1, "follower", now,
        35.0001, 33.0001, 100);
    var sink = new RecordingSink();
    var runner = new FormationCommandRunner(() => [leader, follower], sink);
    var plan = new FormationPlan(leader.Id,
        [new FormationFollower(follower.Id, new FormationOffset(x, y, z))], false, false);

    FormationTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("invalid or excessive", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Setpoints);
  }

  [Fact]
  public void ArduPlaneTargetIsExplicitlyRejected() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        35, 33, 100);
    FormationVehicleSource plane = Source(new MAVLinkInterface(), 2, 1, "plane", now,
        35.0001, 33.0001, 100);
    plane.State.cs.firmware = Firmwares.ArduPlane;
    var sink = new RecordingSink();
    var runner = new FormationCommandRunner(() => [leader, plane], sink);
    var plan = new FormationPlan(leader.Id,
        [new FormationFollower(plane.Id, new FormationOffset(10, 0, 0))], false, false);

    FormationTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("ArduPlane", result.Status, StringComparison.Ordinal);
    Assert.Empty(sink.Setpoints);
  }

  [Fact]
  public void UnsupportedAutopilotFirmwareIsRejected() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        35, 33, 100);
    FormationVehicleSource px4 = Source(new MAVLinkInterface(), 2, 1, "px4", now,
        35.0001, 33.0001, 100);
    px4.State.cs.firmware = Firmwares.PX4;
    var sink = new RecordingSink();
    var runner = new FormationCommandRunner(() => [leader, px4], sink);
    var plan = new FormationPlan(leader.Id,
        [new FormationFollower(px4.Id, new FormationOffset(10, 0, 0))], false, false);

    FormationTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("unsupported", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Setpoints);
  }

  [Fact]
  public async Task RunRequestsLeaderStreamsAndRepeatsAtFormationRate() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        35, 33, 100);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 2, 1, "follower", now,
        35.0001, 33.0001, 100);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var sink = new RecordingSink();
    sink.AfterSetpoint = () => {
      if (sink.Setpoints.Count == 3) {
        cancellation.Cancel();
      }
    };
    var runner = new FormationCommandRunner(() => [leader, follower], sink);
    var plan = new FormationPlan(leader.Id,
        [new FormationFollower(follower.Id, new FormationOffset(10, 0, 0))], false, false);

    string result = await runner.RunAsync(plan, null, cancellation.Token);

    Assert.Equal(3, sink.Setpoints.Count);
    Assert.Equal(leader.Id, Assert.Single(sink.StreamRequests).Id);
    Assert.Contains("operator", result, StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public void WindowLoadsNativeGridAndSafetyControls() {
    var window = new FormationControlWindow();
    try {
      Assert.NotNull(window.FindControl<FormationGridControl>("FormationGrid"));
      Assert.NotNull(window.FindControl<Avalonia.Controls.DataGrid>("FormationVehicleGrid"));
      Assert.NotNull(window.FindControl<Avalonia.Controls.Button>("FormationRunButton"));
    } finally {
      (window.DataContext as IDisposable)?.Dispose();
      window.Close();
    }
  }

  [AvaloniaFact]
  public void ChangingLeaderDoesNotSilentlySelectOldLeaderAsFollower() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource first = Source(new MAVLinkInterface(), 1, 1, "first", now,
        35, 33, 100);
    FormationVehicleSource second = Source(new MAVLinkInterface(), 2, 1, "second", now,
        35.0001, 33.0001, 100);
    using var viewModel = new FormationControlViewModel(
        () => [first, second], new RecordingSink(), (_, _, _) => Task.FromResult(false));
    FormationVehicleItem oldLeader = Assert.IsType<FormationVehicleItem>(
        viewModel.SelectedLeader);
    FormationVehicleItem newLeader = Assert.Single(
        viewModel.Vehicles, row => row.SystemId == 2);

    viewModel.SelectedLeader = newLeader;

    Assert.False(oldLeader.Included);
    Assert.True(newLeader.Included);
  }

  private static FormationVehicleSource Source(
      MAVLinkInterface link,
      byte systemId,
      byte componentId,
      string endpoint,
      DateTime lastPacket,
      double latitude,
      double longitude,
      float altitude,
      float yaw = 0,
      double vx = 0,
      double vy = 0,
      double vz = 0) {
    MAVState state = link.MAVlist[systemId, componentId];
    state.lastvalidpacket = lastPacket;
    state.cs.lat = latitude;
    state.cs.lng = longitude;
    state.cs.alt = altitude;
    state.cs.yaw = yaw;
    state.cs.vx = vx;
    state.cs.vy = vy;
    state.cs.vz = vz;
    state.cs.firmware = Firmwares.ArduCopter2;
    return new FormationVehicleSource(
        new FormationVehicleId(link, systemId, componentId), state, endpoint, IsOpen: true);
  }

  private sealed record FormationSetpointCall(
      FormationVehicleSource Vehicle,
      FormationTarget Target,
      bool AlignYaw,
      bool AimGimbal);

  private sealed class RecordingSink : IFormationCommandSink {
    internal List<FormationSetpointCall> Setpoints { get; } = [];
    internal List<FormationVehicleSource> StreamRequests { get; } = [];
    internal Action? AfterSetpoint { get; set; }

    public void RequestLeaderStreams(FormationVehicleSource leader) =>
        StreamRequests.Add(leader);

    public void SendSetpoint(FormationVehicleSource follower, FormationTarget target,
        bool alignYaw, bool aimGimbal) =>
        RecordSetpoint(follower, target, alignYaw, aimGimbal);

    public bool Arm(FormationVehicleSource vehicle, bool arm) => true;

    public void SetMode(FormationVehicleSource vehicle, string mode) {
    }

    public bool Takeoff(FormationVehicleSource vehicle, double altitudeM) => true;

    private void RecordSetpoint(FormationVehicleSource follower, FormationTarget target,
        bool alignYaw, bool aimGimbal) {
      Setpoints.Add(new FormationSetpointCall(follower, target, alignYaw, aimGimbal));
      AfterSetpoint?.Invoke();
    }
  }
}
