using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public sealed class SwarmFollowPathTests {
  private const double BaseLatitude = 35;
  private const double BaseLongitude = 33;

  [Fact]
  public void TrailResolvesInterpolatedPointBackwardsFromNewestSample() {
    var trail = new FollowPathTrail();
    trail.Record(PointAt(0, 100));
    trail.Record(PointAt(10, 110));
    trail.Record(PointAt(20, 120));

    bool found = trail.TryPointBehind(5, out FollowPathPoint target);

    Assert.True(found);
    Assert.Equal(15, DistanceFromStart(target), 3);
    Assert.Equal(115, target.Altitude, 3);
  }

  [Fact]
  public void TrailWithholdsTargetUntilFullDistanceExists() {
    var trail = new FollowPathTrail();
    trail.Record(PointAt(0, 100));
    trail.Record(PointAt(4, 104));

    Assert.False(trail.TryPointBehind(5, out _));
  }

  [Fact]
  public void SubThresholdLeaderMotionAccumulatesFromLastRecordedPosition() {
    var trail = new FollowPathTrail();

    Assert.Equal(FollowPathTrailUpdate.Added, trail.Record(PointAt(0, 100)));
    Assert.Equal(FollowPathTrailUpdate.Unchanged, trail.Record(PointAt(0.05, 101)));
    Assert.Equal(FollowPathTrailUpdate.Added, trail.Record(PointAt(0.11, 102)));
    Assert.Equal(2, trail.Count);
    Assert.Equal(0.11, trail.LengthM, 2);
  }

  [Fact]
  public void GpsJumpClearsOldTrail() {
    var trail = new FollowPathTrail();
    trail.Record(PointAt(0, 100));
    trail.Record(PointAt(20, 100));

    FollowPathTrailUpdate result = trail.Record(PointAt(600, 100));

    Assert.Equal(FollowPathTrailUpdate.ResetAfterJump, result);
    Assert.Equal(1, trail.Count);
    Assert.Equal(0, trail.LengthM);
    Assert.False(trail.TryPointBehind(1, out _));
  }

  [Theory]
  [InlineData(double.NaN, 33, 100)]
  [InlineData(91, 33, 100)]
  [InlineData(35, 181, 100)]
  [InlineData(35, 33, double.PositiveInfinity)]
  [InlineData(0, 33, 100)]
  public void InvalidTrailPositionIsRejected(double latitude, double longitude, double altitude) {
    var trail = new FollowPathTrail();

    Assert.Throws<ArgumentOutOfRangeException>(() =>
        trail.Record(new FollowPathPoint(latitude, longitude, altitude)));
  }

  [Fact]
  public void TickWithholdsWholeBatchWhileTrailIsTooShort() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        PointAt(0, 100));
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 2, 1, "follower", now,
        PointAt(-10, 100));
    var sink = new RecordingSink();
    var runner = new FollowPathCommandRunner(() => [leader, follower], sink);
    var plan = new FollowPathPlan(leader.Id, [new FollowPathFollower(follower.Id, 1)], 5);

    FollowPathTickResult result = runner.Tick(plan, now);

    Assert.True(result.Continue);
    Assert.Contains("Recording leader trail", result.Status, StringComparison.Ordinal);
    Assert.Empty(result.Commands);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void TickCommandsExplicitFollowersInOrderUsingNewestTrailSection() {
    DateTime now = DateTime.UtcNow;
    FollowPathPoint leaderPoint = PointAt(20, 120);
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        leaderPoint);
    FormationVehicleSource first = Source(new MAVLinkInterface(), 2, 1, "first", now,
        PointAt(-5, 100));
    FormationVehicleSource second = Source(new MAVLinkInterface(), 3, 1, "second", now,
        PointAt(-10, 100));
    var trail = new FollowPathTrail();
    trail.Record(PointAt(0, 100));
    trail.Record(PointAt(10, 110));
    var sink = new RecordingSink();
    var runner = new FollowPathCommandRunner(() => [leader, first, second], sink, trail);
    var plan = new FollowPathPlan(leader.Id,
        [new FollowPathFollower(second.Id, 2), new FollowPathFollower(first.Id, 1)], 5);

    FollowPathTickResult result = runner.Tick(plan, now);

    Assert.True(result.Continue);
    Assert.Collection(sink.Targets,
        call => {
          Assert.Equal(first.Id, call.Vehicle.Id);
          Assert.Equal(15, DistanceFromStart(call.Target), 3);
          Assert.Equal(115, call.Target.Altitude, 3);
        },
        call => {
          Assert.Equal(second.Id, call.Vehicle.Id);
          Assert.Equal(10, DistanceFromStart(call.Target), 3);
          Assert.Equal(110, call.Target.Altitude, 3);
        });
  }

  [Fact]
  public void SameVehicleOnReplacementLinkDoesNotMatchCapturedFollower() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        PointAt(10, 100));
    FormationVehicleSource original = Source(new MAVLinkInterface(), 2, 1, "udp-a", now,
        PointAt(0, 100));
    FormationVehicleSource replacement = Source(new MAVLinkInterface(), 2, 1, "udp-b", now,
        PointAt(0, 100));
    var sink = new RecordingSink();
    var runner = new FollowPathCommandRunner(() => [leader, replacement], sink);
    var plan = new FollowPathPlan(leader.Id, [new FollowPathFollower(original.Id, 1)], 5);

    FollowPathTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("another link", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Theory]
  [InlineData(true, 1, true, "stale")]
  [InlineData(false, 42, true, "not an autopilot")]
  [InlineData(false, 1, false, "closed")]
  public void StaleComponentOrClosedFollowerIsRejectedBeforeSending(
      bool stale, byte componentId, bool isOpen, string expected) {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        PointAt(10, 100));
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 2, componentId, "follower",
        stale ? now.AddMinutes(-1) : now, PointAt(0, 100)) with { IsOpen = isOpen };
    var sink = new RecordingSink();
    var runner = new FollowPathCommandRunner(() => [leader, follower], sink);
    var plan = new FollowPathPlan(leader.Id, [new FollowPathFollower(follower.Id, 1)], 5);

    FollowPathTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains(expected, result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void TickValidatesEveryFollowerBeforeSendingAnyTarget() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        PointAt(20, 100));
    FormationVehicleSource healthy = Source(new MAVLinkInterface(), 2, 1, "healthy", now,
        PointAt(0, 100));
    FormationVehicleSource stale = Source(new MAVLinkInterface(), 3, 1, "stale",
        now.AddMinutes(-1), PointAt(0, 100));
    var trail = new FollowPathTrail();
    trail.Record(PointAt(0, 100));
    var sink = new RecordingSink();
    var runner = new FollowPathCommandRunner(() => [leader, healthy, stale], sink, trail);
    var plan = new FollowPathPlan(leader.Id,
        [new FollowPathFollower(healthy.Id, 1), new FollowPathFollower(stale.Id, 2)], 5);

    FollowPathTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void DuplicateFollowerOrderStopsBeforeSending() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        PointAt(20, 100));
    FormationVehicleSource first = Source(new MAVLinkInterface(), 2, 1, "first", now,
        PointAt(0, 100));
    FormationVehicleSource second = Source(new MAVLinkInterface(), 3, 1, "second", now,
        PointAt(0, 100));
    var sink = new RecordingSink();
    var runner = new FollowPathCommandRunner(() => [leader, first, second], sink);
    var plan = new FollowPathPlan(leader.Id,
        [new FollowPathFollower(first.Id, 1), new FollowPathFollower(second.Id, 1)], 5);

    FollowPathTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("unique", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(501)]
  [InlineData(double.NaN)]
  public void InvalidSeparationStopsBeforeSending(double separation) {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        PointAt(20, 100));
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 2, 1, "follower", now,
        PointAt(0, 100));
    var sink = new RecordingSink();
    var runner = new FollowPathCommandRunner(() => [leader, follower], sink);

    FollowPathTickResult result = runner.Tick(
        new FollowPathPlan(leader.Id, [new FollowPathFollower(follower.Id, 1)], separation), now);

    Assert.False(result.Continue);
    Assert.Contains("separation", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void UnsupportedFirmwareIsRejected() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        PointAt(20, 100));
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 2, 1, "px4", now,
        PointAt(0, 100));
    follower.State.cs.firmware = Firmwares.PX4;
    var sink = new RecordingSink();
    var runner = new FollowPathCommandRunner(() => [leader, follower], sink);

    FollowPathTickResult result = runner.Tick(
        new FollowPathPlan(leader.Id, [new FollowPathFollower(follower.Id, 1)], 5), now);

    Assert.False(result.Continue);
    Assert.Contains("unsupported", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void ArduPlaneRequiresThirtyMetreSeparation() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        PointAt(40, 100));
    FormationVehicleSource plane = Source(new MAVLinkInterface(), 2, 1, "plane", now,
        PointAt(0, 100));
    plane.State.cs.firmware = Firmwares.ArduPlane;
    var sink = new RecordingSink();
    var runner = new FollowPathCommandRunner(() => [leader, plane], sink);

    FollowPathTickResult result = runner.Tick(
        new FollowPathPlan(leader.Id, [new FollowPathFollower(plane.Id, 1)], 29), now);

    Assert.False(result.Continue);
    Assert.Contains("at least 30", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void LeaderGpsJumpStopsWithoutSendingOldTrailTarget() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        PointAt(600, 100));
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 2, 1, "follower", now,
        PointAt(0, 100));
    var trail = new FollowPathTrail();
    trail.Record(PointAt(0, 100));
    trail.Record(PointAt(20, 100));
    var sink = new RecordingSink();
    var runner = new FollowPathCommandRunner(() => [leader, follower], sink, trail);

    FollowPathTickResult result = runner.Tick(
        new FollowPathPlan(leader.Id, [new FollowPathFollower(follower.Id, 1)], 5), now);

    Assert.False(result.Continue);
    Assert.Contains("jumped", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public async Task RunRequestsAllPositionStreamsAndRepeatsAtFiveHertz() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource leader = Source(new MAVLinkInterface(), 1, 1, "leader", now,
        PointAt(20, 120));
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 2, 1, "follower", now,
        PointAt(0, 100));
    var trail = new FollowPathTrail();
    trail.Record(PointAt(0, 100));
    trail.Record(PointAt(10, 110));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var sink = new RecordingSink {
      AfterTarget = count => {
        if (count == 3) {
          cancellation.Cancel();
        }
      },
    };
    var runner = new FollowPathCommandRunner(() => [leader, follower], sink, trail);
    var plan = new FollowPathPlan(leader.Id, [new FollowPathFollower(follower.Id, 1)], 5);

    string result = await runner.RunAsync(plan, null, cancellation.Token);

    Assert.Equal(3, sink.Targets.Count);
    Assert.Collection(Assert.Single(sink.StreamRequests),
        vehicle => Assert.Equal(leader.Id, vehicle.Id),
        vehicle => Assert.Equal(follower.Id, vehicle.Id));
    Assert.Contains("operator", result, StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public void WindowLoadsVehicleGridAndSafetyControls() {
    var window = new SwarmFollowPathWindow();
    try {
      Assert.NotNull(window.FindControl<DataGrid>("FollowPathVehicleGrid"));
      Assert.NotNull(window.FindControl<Button>("FollowPathRunButton"));
    } finally {
      (window.DataContext as IDisposable)?.Dispose();
      window.Close();
    }
  }

  [AvaloniaFact]
  public void ChangingLeaderRemovesOldLeaderAndKeepsAValidFollowerOrder() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource first = Source(new MAVLinkInterface(), 1, 1, "first", now,
        PointAt(10, 100));
    FormationVehicleSource second = Source(new MAVLinkInterface(), 2, 1, "second", now,
        PointAt(0, 100));
    using var viewModel = new SwarmFollowPathViewModel(
        () => [first, second], new RecordingSink(), (_, _, _) => Task.FromResult(false));
    FollowPathVehicleItem oldLeader = Assert.IsType<FollowPathVehicleItem>(
        viewModel.SelectedLeader);
    FollowPathVehicleItem newLeader = Assert.Single(
        viewModel.Vehicles, row => row.SystemId == 2);

    viewModel.SelectedLeader = newLeader;

    Assert.False(oldLeader.Included);
    Assert.True(newLeader.Included);
    Assert.True(oldLeader.Order > 0);
  }

  [AvaloniaFact]
  public void RepeatedLeaderChangesDoNotCreateDuplicateFollowerOrders() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource first = Source(new MAVLinkInterface(), 1, 1, "first", now,
        PointAt(20, 100));
    FormationVehicleSource second = Source(new MAVLinkInterface(), 2, 1, "second", now,
        PointAt(10, 100));
    FormationVehicleSource third = Source(new MAVLinkInterface(), 3, 1, "third", now,
        PointAt(0, 100));
    using var viewModel = new SwarmFollowPathViewModel(
        () => [first, second, third], new RecordingSink(), (_, _, _) => Task.FromResult(false));

    viewModel.SelectedLeader = Assert.Single(viewModel.Vehicles, row => row.SystemId == 2);
    viewModel.SelectedLeader = Assert.Single(viewModel.Vehicles, row => row.SystemId == 3);

    int[] followerOrders = viewModel.Vehicles
        .Where(row => !row.IsLeader)
        .Select(row => row.Order)
        .ToArray();
    Assert.All(followerOrders, order => Assert.True(order > 0));
    Assert.Equal(followerOrders.Length, followerOrders.Distinct().Count());
  }

  private static FollowPathPoint PointAt(double eastM, double altitude) {
    (double latitude, double longitude) = FormationGeometry.Project(
        BaseLatitude, BaseLongitude, Math.PI / 2, eastM);
    return new FollowPathPoint(latitude, longitude, altitude);
  }

  private static double DistanceFromStart(FollowPathPoint point) =>
      FormationGeometry.DistanceAndBearing(
          BaseLatitude, BaseLongitude, point.Latitude, point.Longitude).Distance;

  private static FormationVehicleSource Source(
      MAVLinkInterface link,
      byte systemId,
      byte componentId,
      string endpoint,
      DateTime lastPacket,
      FollowPathPoint point) {
    MAVState state = link.MAVlist[systemId, componentId];
    state.lastvalidpacket = lastPacket;
    state.cs.lat = point.Latitude;
    state.cs.lng = point.Longitude;
    state.cs.alt = (float)point.Altitude;
    state.cs.firmware = Firmwares.ArduCopter2;
    return new FormationVehicleSource(
        new FormationVehicleId(link, systemId, componentId), state, endpoint, IsOpen: true);
  }

  private sealed record TargetCall(FormationVehicleSource Vehicle, FollowPathPoint Target);

  private sealed class RecordingSink : IFollowPathCommandSink {
    internal List<TargetCall> Targets { get; } = [];
    internal List<IReadOnlyList<FormationVehicleSource>> StreamRequests { get; } = [];
    internal Action<int>? AfterTarget { get; init; }

    public void RequestPositionStreams(IReadOnlyList<FormationVehicleSource> vehicles) =>
        StreamRequests.Add(vehicles.ToArray());

    public void SendTarget(FormationVehicleSource follower, FollowPathPoint target) {
      Targets.Add(new TargetCall(follower, target));
      AfterTarget?.Invoke(Targets.Count);
    }

    public bool Arm(FormationVehicleSource vehicle, bool arm) => true;

    public void SetMode(FormationVehicleSource vehicle, string mode) {
    }

    public bool Takeoff(FormationVehicleSource vehicle, double altitudeM) => true;
  }
}
