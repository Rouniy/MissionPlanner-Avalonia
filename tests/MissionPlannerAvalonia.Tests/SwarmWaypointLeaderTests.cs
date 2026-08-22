using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public sealed class SwarmWaypointLeaderTests {
  private const double BaseLatitude = 35;
  private const double BaseLongitude = 33;

  [Fact]
  public void MissionPathIsCompactAndInterpolatesOfficialAltitudeProfile() {
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", DateTime.UtcNow,
        PointAt(0, 10));
    AddMission(air.State, (0, 0), (100, 10), (200, 30));

    Assert.True(WaypointLeaderMissionPath.TryBuild(
        air.State, out WaypointLeaderMissionPath path, out string error), error);
    Assert.Equal(3, path.Profile.Count);
    Assert.Equal(200, path.LengthM, 1);
    Assert.True(path.TryPointAt(150, out FollowPathPoint middle));
    Assert.Equal(20, middle.Altitude, 1);
  }

  [Fact]
  public void MissionSignatureChangesWhenConfirmedMissionChanges() {
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", DateTime.UtcNow,
        PointAt(0, 10));
    AddMission(air.State, (0, 0), (100, 10));
    Assert.True(WaypointLeaderMissionPath.TryBuild(air.State, out var first, out _));

    AddMission(air.State, (0, 0), (110, 10));
    Assert.True(WaypointLeaderMissionPath.TryBuild(air.State, out var second, out _));

    Assert.NotEqual(first.Signature, second.Signature);
  }

  [Fact]
  public void MissionWithoutUsableLegsIsRejected() {
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", DateTime.UtcNow,
        PointAt(0, 10));
    air.State.wps[0] = MissionItem(0, 0, 0, MAVLink.MAV_CMD.WAYPOINT);
    air.State.wps[1] = MissionItem(1, 100, 10, MAVLink.MAV_CMD.DO_CHANGE_SPEED);

    Assert.False(WaypointLeaderMissionPath.TryBuild(air.State, out _, out string error));
    Assert.Contains("no usable", error, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void MissionLegOverFiveKilometresIsIgnoredLikeUpstream() {
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", DateTime.UtcNow,
        PointAt(0, 10));
    AddMission(air.State, (0, 0), (6000, 10));

    Assert.False(WaypointLeaderMissionPath.TryBuild(air.State, out _, out string error));
    Assert.Contains("under 5 km", error, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ClosestPointReportsAlongAndCrossTrackDistances() {
    WaypointLeaderMissionPath path = Path(200);

    Assert.True(path.TryClosest(OffsetPoint(75, 12, 10), out double along, out double away));
    Assert.Equal(75, along, 1);
    Assert.Equal(12, away, 1);
  }

  [Fact]
  public void LineTargetsPreserveAirMasterFirstFlightOrder() {
    WaypointLeaderMissionPath path = Path(200);

    Assert.True(path.TryLineTargets(PointAt(40, 10), 20, 5, 3, out var targets));

    Assert.Equal(new[] { 60d, 55d, 50d },
        targets.Select(DistanceFromStart).Select(value => Math.Round(value)).ToArray());
  }

  [Fact]
  public void VTargetsAlternateLeftAndRightBehindFront() {
    WaypointLeaderMissionPath path = Path(200);

    Assert.True(path.TryVTargets(PointAt(40, 10), 20, 10, 5, out var targets));

    Assert.Equal(5, targets.Count);
    Assert.Equal(60, DistanceFromStart(targets[0]), 1);
    Assert.True(FormationGeometry.DistanceAndBearing(
        targets[1].Latitude, targets[1].Longitude,
        targets[2].Latitude, targets[2].Longitude).Distance > 9);
  }

  [Theory]
  [InlineData(1, 20, 10, 2, 1, true)]
  [InlineData(5, 6000, 10, 2, 1, false)]
  [InlineData(5, 20, 0, 2, 1, false)]
  [InlineData(5, 20, 10, double.NaN, 1, false)]
  [InlineData(5, 20, 10, 2, 101, false)]
  public void SettingsValidationRejectsUnsafeRanges(
      double separation, double lead, double offPath, double altitudeSeparation,
      double acceleration, bool invalidSeparation) {
    bool valid = WaypointLeaderCommandRunner.ValidateSettings(
        new WaypointLeaderSettings(separation, lead, offPath, altitudeSeparation,
            acceleration, false, false), out string error);

    Assert.False(valid);
    Assert.False(string.IsNullOrWhiteSpace(error));
    if (invalidSeparation) {
      Assert.Contains("separation", error, StringComparison.OrdinalIgnoreCase);
    }
  }

  [Fact]
  public void SameSystemOnReplacementLinkDoesNotMatchCapturedAirMaster() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource ground = Source(new MAVLinkInterface(), 1, "ground", now,
        PointAt(0, 10));
    FormationVehicleSource original = Source(new MAVLinkInterface(), 2, "old", now,
        PointAt(20, 10));
    FormationVehicleSource replacement = Source(new MAVLinkInterface(), 2, "new", now,
        PointAt(20, 10));
    AddMission(original.State, (0, 10), (200, 10));
    AddMission(replacement.State, (0, 10), (200, 10));
    WaypointLeaderPlan plan = Plan(ground, original);
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, replacement], sink);

    WaypointLeaderTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("another link", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void NonCopterAirVehicleIsRejectedBeforeCommands() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    air.State.cs.firmware = Firmwares.ArduPlane;
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air], sink);

    WaypointLeaderTickResult result = runner.Tick(Plan(ground, air), now);

    Assert.False(result.Continue);
    Assert.Contains("ArduCopter", result.Status, StringComparison.Ordinal);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void GroundAndAirMasterMustBeDistinct() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", now, PointAt(0, 10));
    AddMission(air.State, (0, 10), (200, 10));
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [air], sink);
    WaypointLeaderMissionPath.TryBuild(air.State, out var path, out _);
    var plan = new WaypointLeaderPlan(air.Id, air.Id, [], Settings(), path.Signature);

    WaypointLeaderTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("different", result.Status, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void DuplicateFollowerOrderStopsWholeBatch() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    FormationVehicleSource first = Source(new MAVLinkInterface(), 3, "first", now, PointAt(5, 10));
    FormationVehicleSource second = Source(new MAVLinkInterface(), 4, "second", now, PointAt(10, 10));
    WaypointLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with {
      Followers = [
        new WaypointLeaderFollower(first.Id, 1), new WaypointLeaderFollower(second.Id, 1),
      ],
    };
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air, first, second], sink);

    WaypointLeaderTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("unique", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void EveryFollowerIsValidatedBeforeAnyCommand() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    FormationVehicleSource healthy = Source(new MAVLinkInterface(), 3, "healthy", now,
        PointAt(5, 10));
    FormationVehicleSource stale = Source(new MAVLinkInterface(), 4, "stale", now.AddMinutes(-1),
        PointAt(10, 10));
    WaypointLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with {
      Followers = [
        new WaypointLeaderFollower(healthy.Id, 1), new WaypointLeaderFollower(stale.Id, 2),
      ],
    };
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air, healthy, stale], sink);

    WaypointLeaderTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("stale", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
    Assert.Empty(sink.Modes);
  }

  [Fact]
  public void InitializationWritesOnlyAvailableOfficialFlightParameters() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 3, "follower", now,
        PointAt(5, 10));
    air.State.param.Add(new MAVLink.MAVLinkParam("RTL_ALT", 1500,
        MAVLink.MAV_PARAM_TYPE.REAL32));
    air.State.param.Add(new MAVLink.MAVLinkParam("WPNAV_ACCEL", 250,
        MAVLink.MAV_PARAM_TYPE.REAL32));
    follower.State.param.Add(new MAVLink.MAVLinkParam("RTL_ALT_M", 15,
        MAVLink.MAV_PARAM_TYPE.REAL32));
    follower.State.param.Add(new MAVLink.MAVLinkParam("WP_ACC", 2,
        MAVLink.MAV_PARAM_TYPE.REAL32));
    WaypointLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with {
      Followers = [new WaypointLeaderFollower(follower.Id, 1)],
    };
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air, follower], sink);

    WaypointLeaderTickResult result = runner.Tick(plan, now);

    Assert.Equal(WaypointLeaderMode.Takeoff, result.Mode);
    Assert.Equal(new[] {
      (air.Id, "RTL_ALT", 0d), (air.Id, "WPNAV_ACCEL", 100d),
      (follower.Id, "RTL_ALT_M", 0d), (follower.Id, "WP_ACC", 1d),
    }, sink.Parameters);
    Assert.DoesNotContain(sink.Parameters, call => call.Item1 == ground.Id);
  }

  [Fact]
  public void RejectedInitialParameterStopsBeforeArmOrTarget() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    air.State.param.Add(new MAVLink.MAVLinkParam("RTL_ALT", 1500,
        MAVLink.MAV_PARAM_TYPE.REAL32));
    var sink = new RecordingSink { AcceptParameters = false };
    var runner = new WaypointLeaderCommandRunner(() => [ground, air], sink);

    WaypointLeaderTickResult result = runner.Tick(Plan(ground, air), now);

    Assert.False(result.Continue);
    Assert.Empty(sink.Arms);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void TakeoffCommandsOnlyAirMasterAndExplicitFollowers() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 3, "follower", now,
        PointAt(5, 0));
    WaypointLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with { Followers = [new WaypointLeaderFollower(follower.Id, 1)] };
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air, follower], sink);
    runner.Tick(plan, now);

    WaypointLeaderTickResult result = runner.Tick(plan, now);

    Assert.Equal(WaypointLeaderMode.Takeoff, result.Mode);
    Assert.Equal(new[] { air.Id, follower.Id }, sink.Arms.Select(call => call.Vehicle).ToArray());
    Assert.Equal(new[] { air.Id, follower.Id }, sink.Takeoffs.Select(call => call.Vehicle).ToArray());
    Assert.DoesNotContain(sink.Modes, call => call.Vehicle == ground.Id);
    Assert.DoesNotContain(sink.Targets, call => call.Vehicle.Id == ground.Id);
  }

  [Fact]
  public void StagedTakeoffTransitionsToFlightOnlyWhenWholeGroupIsAtTargets() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 3, "follower", now,
        PointAt(10, 12));
    SetPosition(air, PointAt(15, 10));
    air.State.cs.armed = true;
    follower.State.cs.armed = true;
    WaypointLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with { Followers = [new WaypointLeaderFollower(follower.Id, 1)] };
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air, follower], sink);
    runner.Tick(plan, now);

    WaypointLeaderTickResult result = runner.Tick(plan, now);

    Assert.Equal(WaypointLeaderMode.FlyToGroundMaster, result.Mode);
    Assert.Equal(2, result.Commands.Count);
  }

  [Fact]
  public void FollowModeUsesOneThirdGroundVelocityAndOrderedTargets() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 3, "follower", now,
        PointAt(10, 12));
    ground.State.cs.vx = 6;
    ground.State.cs.vy = 3;
    ground.State.cs.vz = -0.9;
    WaypointLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with { Followers = [new WaypointLeaderFollower(follower.Id, 1)] };
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air, follower], sink);
    runner.Tick(plan, now);
    SetPosition(air, PointAt(15, 10));
    SetPosition(follower, PointAt(10, 12));
    air.State.cs.armed = follower.State.cs.armed = true;
    runner.Tick(plan, now);
    SetPosition(air, PointAt(20, 10));
    SetPosition(follower, PointAt(15, 10));
    runner.Tick(plan, now);

    WaypointLeaderTickResult result = runner.Tick(plan, now);

    Assert.Equal(WaypointLeaderMode.FollowGroundMaster, result.Mode);
    Assert.Collection(result.Commands,
        command => {
          Assert.Equal(air.Id, command.Vehicle.Id);
          Assert.Equal(20, DistanceFromStart(command.Target), 1);
          Assert.Equal(2, command.VelocityNorth);
          Assert.Equal(1, command.VelocityEast);
          Assert.Equal(-0.3, command.VelocityDown, 5);
        },
        command => {
          Assert.Equal(follower.Id, command.Vehicle.Id);
          Assert.Equal(15, DistanceFromStart(command.Target), 1);
        });
  }

  [Fact]
  public void MissionEditAfterConfirmationStopsBeforeAnyCommand() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    WaypointLeaderPlan plan = Plan(ground, air);
    AddMission(air.State, (0, 10), (210, 10));
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air], sink);

    WaypointLeaderTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("mission changed", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Parameters);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void CollisionOverridePausesStateMachineAndCommandsSeparation() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 3, "follower", now,
        PointAt(20.5, 10));
    SetPosition(air, PointAt(20, 10));
    air.State.cs.armed = follower.State.cs.armed = true;
    WaypointLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with { Followers = [new WaypointLeaderFollower(follower.Id, 1)] };
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air, follower], sink);

    WaypointLeaderTickResult result = runner.Tick(plan, now);

    Assert.Equal(WaypointLeaderMode.Idle, result.Mode);
    Assert.Contains("Collision", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Single(result.Commands);
    Assert.Empty(sink.Parameters);
  }

  [Fact]
  public void LandAltitudeTargetsDoNotRatchetOnRepeatedTicks() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 3, "follower", now,
        PointAt(20, 5));
    air.State.cs.armed = follower.State.cs.armed = true;
    WaypointLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with { Followers = [new WaypointLeaderFollower(follower.Id, 1)] };
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air, follower], sink);
    runner.Tick(plan, now);
    runner.RequestMode(WaypointLeaderMode.LandAltitude);

    WaypointLeaderTickResult first = runner.Tick(plan, now);
    WaypointLeaderTickResult second = runner.Tick(plan, now);

    Assert.Equal(WaypointLeaderMode.LandAltitude, first.Mode);
    Assert.Equal(WaypointLeaderMode.LandAltitude, second.Mode);
    Assert.Equal(first.Commands.Select(command => command.Target.Altitude),
        second.Commands.Select(command => command.Target.Altitude));
  }

  [Fact]
  public void SeparatedAltitudeCompletionIssuesRtlAndLandingStopsWhenDisarmed() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    air.State.cs.armed = false;
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air], sink);
    WaypointLeaderPlan plan = Plan(ground, air);
    runner.Tick(plan, now);
    runner.RequestMode(WaypointLeaderMode.LandAltitude);

    WaypointLeaderTickResult rtl = runner.Tick(plan, now);
    WaypointLeaderTickResult complete = runner.Tick(plan, now);

    Assert.Equal(WaypointLeaderMode.Landing, rtl.Mode);
    Assert.Contains(sink.Modes, call => call.Vehicle == air.Id && call.Mode == "RTL");
    Assert.False(complete.Continue);
    Assert.Contains("completed", complete.Status, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task RunRequestsGroundAndEveryAirPositionStreamAtTenHertz() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    int ticks = 0;
    var sink = new RecordingSink();
    var runner = new WaypointLeaderCommandRunner(() => [ground, air], sink);

    string result = await runner.RunAsync(Plan(ground, air), _ => {
      if (Interlocked.Increment(ref ticks) == 3) {
        cancellation.Cancel();
      }
    }, cancellation.Token);

    Assert.Equal(3, ticks);
    Assert.Collection(Assert.Single(sink.StreamRequests),
        vehicle => Assert.Equal(ground.Id, vehicle.Id),
        vehicle => Assert.Equal(air.Id, vehicle.Id));
    Assert.Contains("operator", result, StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public void WindowLoadsGridProfileAndSafetyControls() {
    var window = new SwarmWaypointLeaderWindow();
    try {
      Assert.NotNull(window.FindControl<DataGrid>("WaypointLeaderVehicleGrid"));
      Assert.NotNull(window.FindControl<WaypointLeaderProfileControl>(
          "WaypointLeaderProfile"));
      Assert.NotNull(window.FindControl<Button>("WaypointLeaderRunButton"));
    } finally {
      (window.DataContext as IDisposable)?.Dispose();
      window.Close();
    }
  }

  [AvaloniaFact]
  public void ViewModelKeepsMastersOutOfFollowerSelectionAndBuildsExactPlan() {
    DateTime now = DateTime.UtcNow;
    (FormationVehicleSource ground, FormationVehicleSource air) = Group(now);
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 3, "follower", now,
        PointAt(10, 10));
    using var viewModel = new SwarmWaypointLeaderViewModel(
        () => [ground, air, follower], new RecordingSink(),
        (_, _, _) => Task.FromResult(false));
    WaypointLeaderVehicleItem followerRow = Assert.Single(
        viewModel.Vehicles, row => row.SystemId == 3);
    followerRow.Included = true;

    Assert.True(viewModel.TryBuildPlan(out WaypointLeaderPlan plan, out string error), error);
    Assert.Equal(ground.Id, plan.GroundMaster);
    Assert.Equal(air.Id, plan.AirMaster);
    Assert.Equal(follower.Id, Assert.Single(plan.Followers).Id);
    Assert.False(viewModel.SelectedGroundMaster!.Included);
    Assert.False(viewModel.SelectedAirMaster!.Included);
  }

  private static (FormationVehicleSource Ground, FormationVehicleSource Air) Group(DateTime now) {
    FormationVehicleSource ground = Source(new MAVLinkInterface(), 1, "ground", now,
        PointAt(0, 10));
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", now,
        PointAt(20, 0));
    AddMission(air.State, (0, 10), (200, 10));
    return (ground, air);
  }

  private static WaypointLeaderMissionPath Path(double length) {
    FormationVehicleSource source = Source(new MAVLinkInterface(), 2, "air", DateTime.UtcNow,
        PointAt(0, 10));
    AddMission(source.State, (0, 10), (length, 10));
    Assert.True(WaypointLeaderMissionPath.TryBuild(
        source.State, out WaypointLeaderMissionPath path, out string error), error);
    return path;
  }

  private static WaypointLeaderPlan Plan(
      FormationVehicleSource ground, FormationVehicleSource air) {
    Assert.True(WaypointLeaderMissionPath.TryBuild(
        air.State, out WaypointLeaderMissionPath path, out string error), error);
    return new WaypointLeaderPlan(ground.Id, air.Id, [], Settings(), path.Signature);
  }

  private static WaypointLeaderSettings Settings() =>
      new(5, 20, 10, 2, 1, false, false);

  private static void AddMission(
      MAVState state, params (double EastM, double AltitudeM)[] points) {
    state.wps.Clear();
    for (int index = 0; index < points.Length; index++) {
      state.wps[index] = MissionItem(index, points[index].EastM,
          points[index].AltitudeM, MAVLink.MAV_CMD.WAYPOINT);
    }
  }

  private static MAVLink.mavlink_mission_item_int_t MissionItem(
      int sequence, double eastM, double altitudeM, MAVLink.MAV_CMD command) {
    FollowPathPoint point = PointAt(eastM, altitudeM);
    return new MAVLink.mavlink_mission_item_int_t {
      seq = (ushort)sequence,
      command = (ushort)command,
      frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
      x = (int)Math.Round(point.Latitude * 1e7),
      y = (int)Math.Round(point.Longitude * 1e7),
      z = (float)altitudeM,
      target_system = 2,
      target_component = 1,
    };
  }

  private static FormationVehicleSource Source(
      MAVLinkInterface link, byte systemId, string endpoint,
      DateTime lastPacket, FollowPathPoint point) {
    MAVState state = link.MAVlist[systemId, 1];
    state.lastvalidpacket = lastPacket;
    state.cs.lat = point.Latitude;
    state.cs.lng = point.Longitude;
    state.cs.alt = (float)point.Altitude;
    state.cs.firmware = Firmwares.ArduCopter2;
    return new FormationVehicleSource(
        new FormationVehicleId(link, systemId, 1), state, endpoint, IsOpen: true);
  }

  private static void SetPosition(FormationVehicleSource source, FollowPathPoint point) {
    source.State.cs.lat = point.Latitude;
    source.State.cs.lng = point.Longitude;
    source.State.cs.alt = (float)point.Altitude;
  }

  private static FollowPathPoint PointAt(double eastM, double altitude) =>
      OffsetPoint(eastM, 0, altitude);

  private static FollowPathPoint OffsetPoint(double eastM, double northM, double altitude) {
    (double latitude, double longitude) = FormationGeometry.Project(
        BaseLatitude, BaseLongitude, Math.Atan2(eastM, northM),
        Math.Sqrt(eastM * eastM + northM * northM));
    return new FollowPathPoint(latitude, longitude, altitude);
  }

  private static double DistanceFromStart(FollowPathPoint point) =>
      FormationGeometry.DistanceAndBearing(
          BaseLatitude, BaseLongitude, point.Latitude, point.Longitude).Distance;

  private sealed record ModeCall(FormationVehicleId Vehicle, string Mode);
  private sealed record ArmCall(FormationVehicleId Vehicle, bool Arm);
  private sealed record TakeoffCall(FormationVehicleId Vehicle, double AltitudeM);

  private sealed class RecordingSink : IWaypointLeaderCommandSink {
    internal List<IReadOnlyList<FormationVehicleSource>> StreamRequests { get; } = [];
    internal List<(FormationVehicleId, string, double)> Parameters { get; } = [];
    internal List<ArmCall> Arms { get; } = [];
    internal List<ModeCall> Modes { get; } = [];
    internal List<TakeoffCall> Takeoffs { get; } = [];
    internal List<WaypointLeaderCommand> Targets { get; } = [];
    internal bool AcceptParameters { get; init; } = true;

    public void RequestPositionStreams(IReadOnlyList<FormationVehicleSource> vehicles) =>
        StreamRequests.Add(vehicles.ToArray());

    public bool SetParameter(FormationVehicleSource vehicle, string name, double value) {
      Parameters.Add((vehicle.Id, name, value));
      return AcceptParameters;
    }

    public bool Arm(FormationVehicleSource vehicle, bool arm) {
      Arms.Add(new ArmCall(vehicle.Id, arm));
      return true;
    }

    public void SetMode(FormationVehicleSource vehicle, string mode) =>
        Modes.Add(new ModeCall(vehicle.Id, mode));

    public bool Takeoff(FormationVehicleSource vehicle, double altitudeM) {
      Takeoffs.Add(new TakeoffCall(vehicle.Id, altitudeM));
      return true;
    }

    public void SendTarget(WaypointLeaderCommand command) => Targets.Add(command);
  }
}
