using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public sealed class SwarmFollowLeaderSequenceTests {
  private const double BaseLatitude = 35;
  private const double BaseLongitude = 33;

  [Fact]
  public void FollowLeaderSendsOfficialAirMasterTargetAndVelocityFactors() {
    Assert.Equal(21, FollowLeaderCommandRunner.MaximumFollowers);
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource ground = Source(new MAVLinkInterface(), 1, "ground", now,
        PointAt(0, 3), Firmwares.ArduRover);
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", now,
        PointAt(20, 10));
    ground.State.cs.vx = 4;
    ground.State.cs.vy = 0;
    ground.State.cs.vz = -1;
    var sink = new RecordingSink();
    var runner = new FollowLeaderCommandRunner(() => [ground, air], sink);

    FollowLeaderTickResult result = runner.Tick(Plan(ground, air), now);

    Assert.True(result.Continue);
    FollowLeaderCommand command = Assert.Single(result.Commands);
    Assert.Equal(air.Id, command.Vehicle.Id);
    Assert.Equal(0, EastFromStart(command.Target), 1);
    Assert.Equal(5, NorthFromStart(command.Target), 1);
    Assert.Equal(10, command.Target.Altitude);
    Assert.Equal(2.4, command.Velocity.North, 5);
    Assert.Equal(0, command.Velocity.East);
    Assert.Equal(-0.6, command.Velocity.Down, 5);
    Assert.Single(sink.Targets);
  }

  [Fact]
  public void FollowLeaderWaitsForWholeOrderedTrailBeforeSendingBatch() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource ground = Source(new MAVLinkInterface(), 1, "ground", now,
        PointAt(0, 2), Firmwares.ArduRover);
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", now, PointAt(20, 10));
    FormationVehicleSource first = Source(new MAVLinkInterface(), 3, "first", now,
        PointAt(-5, 10));
    FormationVehicleSource second = Source(new MAVLinkInterface(), 4, "second", now,
        PointAt(-10, 10));
    FollowLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with {
      Followers = [
        new FollowLeaderFollower(first.Id, 1),
        new FollowLeaderFollower(second.Id, 2),
      ],
    };
    var sink = new RecordingSink();
    var runner = new FollowLeaderCommandRunner(() => [ground, air, first, second], sink);

    FollowLeaderTickResult waiting = runner.Tick(plan, now);
    SetPosition(ground, PointAt(6, 2));
    FollowLeaderTickResult sent = runner.Tick(plan, now);

    Assert.Empty(waiting.Commands);
    Assert.Contains("Recording", waiting.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(3, sent.Commands.Count);
    Assert.Equal(new[] { air.Id, first.Id, second.Id },
        sent.Commands.Select(command => command.Vehicle.Id).ToArray());
    Assert.Equal(6, EastFromStart(sent.Commands[1].Target), 1);
    Assert.Equal(1, EastFromStart(sent.Commands[2].Target), 1);
    Assert.Equal(12, sent.Commands[1].Target.Altitude, 3);
  }

  [Fact]
  public void FollowLeaderAppliesOfficialMissionTurnCorrectionNearWaypoint() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource ground = Source(new MAVLinkInterface(), 1, "ground", now,
        PointAt(0, 2), Firmwares.ArduRover);
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", now, PointAt(20, 10));
    ground.State.cs.vx = -2;
    ground.State.cs.vy = 0;
    ground.State.cs.groundspeed = 3;
    ground.State.cs.wp_dist = 1;
    ground.State.cs.wpno = 0;
    ground.State.wps[0] = MissionItem(0, 0);
    ground.State.wps[1] = MissionItem(1, 100);
    var runner = new FollowLeaderCommandRunner(() => [ground, air], new RecordingSink());

    FollowLeaderCommand command = Assert.Single(runner.Tick(Plan(ground, air), now).Commands);

    Assert.Equal(5, EastFromStart(command.Target), 1);
    Assert.Equal(0, NorthFromStart(command.Target), 1);
    Assert.Equal(0, command.Velocity.North, 2);
    Assert.Equal(1.8, command.Velocity.East, 2);
  }

  [Fact]
  public void FollowLeaderRejectsReplacementLinkBeforeAnyCommand() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource ground = Source(new MAVLinkInterface(), 1, "ground", now,
        PointAt(0, 2), Firmwares.ArduRover);
    FormationVehicleSource original = Source(new MAVLinkInterface(), 2, "old", now,
        PointAt(20, 10));
    FormationVehicleSource replacement = Source(new MAVLinkInterface(), 2, "new", now,
        PointAt(20, 10));
    var sink = new RecordingSink();
    var runner = new FollowLeaderCommandRunner(() => [ground, replacement], sink);

    FollowLeaderTickResult result = runner.Tick(Plan(ground, original), now);

    Assert.False(result.Continue);
    Assert.Contains("another link", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void FollowLeaderValidatesEveryFollowerBeforeAnyCommand() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource ground = Source(new MAVLinkInterface(), 1, "ground", now,
        PointAt(0, 2), Firmwares.ArduRover);
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", now, PointAt(20, 10));
    FormationVehicleSource fresh = Source(new MAVLinkInterface(), 3, "fresh", now,
        PointAt(-5, 10));
    FormationVehicleSource stale = Source(new MAVLinkInterface(), 4, "stale",
        now.AddMinutes(-1), PointAt(-10, 10));
    FollowLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with {
      Followers = [
        new FollowLeaderFollower(fresh.Id, 1),
        new FollowLeaderFollower(stale.Id, 2),
      ],
    };
    var sink = new RecordingSink();
    var runner = new FollowLeaderCommandRunner(() => [ground, air, fresh, stale], sink);

    FollowLeaderTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("stale", result.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
  }

  [Fact]
  public void FollowLeaderRequiresContiguousFollowerOrder() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource ground = Source(new MAVLinkInterface(), 1, "ground", now,
        PointAt(0, 2), Firmwares.ArduRover);
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", now, PointAt(20, 10));
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 3, "follower", now,
        PointAt(-5, 10));
    FollowLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with {
      Followers = [new FollowLeaderFollower(follower.Id, 2)],
    };
    var runner = new FollowLeaderCommandRunner(
        () => [ground, air, follower], new RecordingSink());

    FollowLeaderTickResult result = runner.Tick(plan, now);

    Assert.False(result.Continue);
    Assert.Contains("contiguous", result.Status, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task FollowLeaderRunRequestsEveryRoleAtTenHertz() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource ground = Source(new MAVLinkInterface(), 1, "ground", now,
        PointAt(0, 2), Firmwares.ArduRover);
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", now, PointAt(20, 10));
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 3, "follower", now,
        PointAt(-5, 10));
    FollowLeaderPlan basePlan = Plan(ground, air);
    var plan = basePlan with {
      Followers = [new FollowLeaderFollower(follower.Id, 1)],
    };
    var sink = new RecordingSink();
    var runner = new FollowLeaderCommandRunner(() => [ground, air, follower], sink);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    int ticks = 0;

    string result = await runner.RunAsync(plan, _ => {
      if (Interlocked.Increment(ref ticks) == 3) {
        cancellation.Cancel();
      }
    }, cancellation.Token);

    Assert.Equal(3, ticks);
    Assert.Collection(Assert.Single(sink.StreamRequests),
        item => Assert.Equal(ground.Id, item.Id),
        item => Assert.Equal(air.Id, item.Id),
        item => Assert.Equal(follower.Id, item.Id));
    Assert.Contains("operator", result, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void SequenceJsonRoundTripMatchesOfficialFieldShape() {
    string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
    try {
      var document = new SwarmSequenceDocument {
        Layouts = [new SwarmSequenceLayout {
          Id = "Line",
          DelayStart = 2,
          DelayEnd = 3,
          Offset = new Dictionary<int, SwarmSequenceOffset> {
            [1] = new SwarmSequenceOffset(1.5, -2, 7),
          },
        }],
        Steps = ["Line", "Line"],
      };

      SwarmSequenceFile.Save(path, document);
      string json = File.ReadAllText(path);
      SwarmSequenceDocument loaded = SwarmSequenceFile.Load(path);

      Assert.Contains("\"Layouts\"", json, StringComparison.Ordinal);
      Assert.Contains("\"Offset\"", json, StringComparison.Ordinal);
      Assert.Contains("\"x\"", json, StringComparison.Ordinal);
      Assert.DoesNotContain("\"X\"", json, StringComparison.Ordinal);
      Assert.Equal(2, loaded.Steps.Count);
      Assert.Equal(new SwarmSequenceOffset(1.5, -2, 7), loaded.Layouts[0].Offset[1]);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void SequenceLoadsOfficialNewtonsoftJson() {
    string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
    try {
      File.WriteAllText(path, """
          {
            "Layouts": [
              { "Id": "V", "DelayStart": 0, "DelayEnd": 0,
                "Offset": { "7": { "x": 4.0, "y": 5.0, "z": 6.0 } } }
            ],
            "Steps": ["V"]
          }
          """);

      SwarmSequenceDocument loaded = SwarmSequenceFile.Load(path);

      Assert.Equal("V", loaded.Layouts[0].Id);
      Assert.Equal(new SwarmSequenceOffset(4, 5, 6), loaded.Layouts[0].Offset[7]);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void SequenceRejectsAmbiguousLayoutIdsAndMissingSteps() {
    var duplicate = new SwarmSequenceDocument {
      Layouts = [new() { Id = "A" }, new() { Id = "A" }],
    };
    var missing = new SwarmSequenceDocument {
      Layouts = [new() { Id = "A" }],
      Steps = ["B"],
    };

    Assert.Throws<InvalidDataException>(() => SwarmSequenceFile.Validate(duplicate));
    Assert.Throws<InvalidDataException>(() => SwarmSequenceFile.Validate(missing));
  }

  [Fact]
  public void SequenceRejectsLayoutsWithDifferentVehicleSlots() {
    var document = new SwarmSequenceDocument {
      Layouts = [
        new SwarmSequenceLayout {
          Id = "A",
          Offset = new Dictionary<int, SwarmSequenceOffset> { [1] = new(0, 0, 5) },
        },
        new SwarmSequenceLayout {
          Id = "B",
          Offset = new Dictionary<int, SwarmSequenceOffset> { [2] = new(0, 0, 5) },
        },
      ],
    };

    InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
        SwarmSequenceFile.Validate(document));

    Assert.Contains("same MAVLink system ids", error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void SequenceSendsOfficialEastNorthOffsetAndAbsoluteRelativeAltitude() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource anchor = Source(new MAVLinkInterface(), 1, "anchor", now,
        PointAt(0, 5));
    FormationVehicleSource vehicle = Source(new MAVLinkInterface(), 2, "vehicle", now,
        PointAt(2, 5));
    var layout = new SwarmSequenceLayout {
      Id = "Offset",
      Offset = new Dictionary<int, SwarmSequenceOffset> {
        [7] = new SwarmSequenceOffset(12, 9, 20),
      },
    };
    var plan = new SwarmSequenceCommandPlan(
        anchor.Id, new SwarmSequenceOrigin(BaseLatitude, BaseLongitude), layout,
        [new SwarmSequenceAssignment(7, vehicle.Id)]);
    var sink = new RecordingSink();
    var runner = new SwarmSequenceCommandRunner(() => [anchor, vehicle], sink);

    SwarmSequenceCommandResult result = runner.SendLayout(plan, now);

    SwarmSequenceCommand command = Assert.Single(result.Commands);
    Assert.Equal(12, EastFromStart(command.Target), 1);
    Assert.Equal(9, NorthFromStart(command.Target), 1);
    Assert.Equal(20, command.Target.Altitude);
    Assert.Equal(new FollowLeaderVelocity(0, 0, 0), Assert.Single(sink.Targets).Velocity);
    Assert.Equal(vehicle.Id, Assert.Single(Assert.Single(sink.StreamRequests)).Id);
  }

  [Fact]
  public void SequenceRejectsReplacementLinkAndSendsNoPartialBatch() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource anchor = Source(new MAVLinkInterface(), 1, "anchor", now,
        PointAt(0, 5));
    FormationVehicleSource original = Source(new MAVLinkInterface(), 2, "old", now,
        PointAt(2, 5));
    FormationVehicleSource replacement = Source(new MAVLinkInterface(), 2, "new", now,
        PointAt(2, 5));
    var layout = new SwarmSequenceLayout {
      Id = "L",
      Offset = new Dictionary<int, SwarmSequenceOffset> { [2] = new(0, 0, 5) },
    };
    var plan = new SwarmSequenceCommandPlan(
        anchor.Id, new(BaseLatitude, BaseLongitude), layout,
        [new SwarmSequenceAssignment(2, original.Id)]);
    var sink = new RecordingSink();
    var runner = new SwarmSequenceCommandRunner(() => [anchor, replacement], sink);

    InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
        runner.SendLayout(plan, now));

    Assert.Contains("another link", exception.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(sink.Targets);
    Assert.Empty(sink.StreamRequests);
  }

  [Fact]
  public void SequenceRejectsOneVehicleMappedToMultipleSlots() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource anchor = Source(new MAVLinkInterface(), 1, "anchor", now,
        PointAt(0, 5));
    FormationVehicleSource vehicle = Source(new MAVLinkInterface(), 2, "vehicle", now,
        PointAt(2, 5));
    var layout = new SwarmSequenceLayout {
      Id = "L",
      Offset = new Dictionary<int, SwarmSequenceOffset> {
        [2] = new(0, 0, 5),
        [3] = new(5, 0, 5),
      },
    };
    var plan = new SwarmSequenceCommandPlan(
        anchor.Id, new(BaseLatitude, BaseLongitude), layout,
        [new(2, vehicle.Id), new(3, vehicle.Id)]);
    var runner = new SwarmSequenceCommandRunner(() => [anchor, vehicle], new RecordingSink());

    Assert.False(runner.TryResolvePlan(plan, now, out _, out string error));
    Assert.Contains("multiple", error, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void SequenceOriginIsCapturedFromExactFreshAnchor() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource anchor = Source(new MAVLinkInterface(), 1, "anchor", now,
        PointAt(17, 5));
    var runner = new SwarmSequenceCommandRunner(() => [anchor], new RecordingSink());

    Assert.True(runner.TryCaptureOrigin(anchor.Id, now,
        out SwarmSequenceOrigin origin, out string error), error);
    Assert.Equal(anchor.State.cs.lat, origin.Latitude);
    Assert.Equal(anchor.State.cs.lng, origin.Longitude);
  }

  [AvaloniaFact]
  public void FollowLeaderWindowLoadsSafetyAndVehicleControls() {
    var window = new SwarmFollowLeaderWindow();
    try {
      Assert.NotNull(window.FindControl<DataGrid>("FollowLeaderVehicleGrid"));
      Assert.NotNull(window.FindControl<Button>("FollowLeaderRunButton"));
    } finally {
      (window.DataContext as IDisposable)?.Dispose();
      window.Close();
    }
  }

  [AvaloniaFact]
  public void SequenceWindowLoadsEditorAssignmentAndRunControls() {
    var window = new SwarmSequenceWindow();
    try {
      Assert.NotNull(window.FindControl<SequenceLayoutControl>("SequenceLayoutGrid"));
      Assert.NotNull(window.FindControl<DataGrid>("SequenceOffsetGrid"));
      Assert.NotNull(window.FindControl<DataGrid>("SequenceAssignmentGrid"));
      Assert.NotNull(window.FindControl<Button>("SequenceRunStepButton"));
    } finally {
      (window.DataContext as IDisposable)?.Dispose();
      window.Close();
    }
  }

  [AvaloniaFact]
  public void FollowLeaderViewModelBuildsExactRolesAndOrderedFollowers() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource ground = Source(new MAVLinkInterface(), 1, "ground", now,
        PointAt(0, 2), Firmwares.ArduRover);
    FormationVehicleSource air = Source(new MAVLinkInterface(), 2, "air", now, PointAt(20, 10));
    FormationVehicleSource follower = Source(new MAVLinkInterface(), 3, "follower", now,
        PointAt(-5, 10));
    using var viewModel = new SwarmFollowLeaderViewModel(
        () => [ground, air, follower], new RecordingSink(),
        (_, _, _) => Task.FromResult(false));
    WaypointLeaderVehicleItem row = Assert.Single(
        viewModel.Vehicles, item => item.SystemId == 3);
    row.Included = true;

    Assert.True(viewModel.TryBuildPlan(out FollowLeaderPlan plan, out string error), error);
    Assert.Equal(ground.Id, plan.GroundMaster);
    Assert.Equal(air.Id, plan.AirMaster);
    Assert.Equal(follower.Id, Assert.Single(plan.Followers).Id);
    Assert.False(viewModel.SelectedGroundMaster!.Included);
    Assert.False(viewModel.SelectedAirMaster!.Included);
  }

  [AvaloniaFact]
  public async Task SequenceViewModelDoesNotGuessDuplicateSysidAcrossModems() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource first = Source(new MAVLinkInterface(), 7, "udp-a", now,
        PointAt(0, 5));
    FormationVehicleSource second = Source(new MAVLinkInterface(), 7, "udp-b", now,
        PointAt(2, 5));
    string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
    try {
      SwarmSequenceFile.Save(path, new SwarmSequenceDocument {
        Layouts = [new SwarmSequenceLayout {
          Id = "L",
          Offset = new Dictionary<int, SwarmSequenceOffset> { [7] = new(0, 0, 5) },
        }],
        Steps = ["L"],
      });
      using var viewModel = new SwarmSequenceViewModel(
          () => [first, second], new RecordingSink(),
          (_, _, _) => Task.FromResult(false));

      await viewModel.LoadAsync(path);

      Assert.Equal(2, viewModel.VehicleOptions.Count);
      Assert.Null(Assert.Single(viewModel.Assignments).SelectedVehicle);
    } finally {
      File.Delete(path);
    }
  }

  [AvaloniaFact]
  public async Task SequenceViewModelRunsConfirmedStepForExactAssignment() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource anchor = Source(new MAVLinkInterface(), 1, "anchor", now,
        PointAt(0, 5));
    FormationVehicleSource vehicle = Source(new MAVLinkInterface(), 2, "vehicle", now,
        PointAt(2, 5));
    string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
    try {
      SwarmSequenceFile.Save(path, new SwarmSequenceDocument {
        Layouts = [new SwarmSequenceLayout {
          Id = "L",
          Offset = new Dictionary<int, SwarmSequenceOffset> { [2] = new(4, 3, 7) },
        }],
        Steps = ["L"],
      });
      var sink = new RecordingSink();
      using var viewModel = new SwarmSequenceViewModel(
          () => [anchor, vehicle], sink,
          (_, _, _) => Task.FromResult(true));
      await viewModel.LoadAsync(path);

      await viewModel.RunStepCommand.ExecuteAsync(null);

      Assert.Equal(vehicle.Id, Assert.Single(sink.Targets).Vehicle.Id);
      Assert.Contains("Sequence layout 'L'", viewModel.Status, StringComparison.Ordinal);
      Assert.Equal("Step 1 / 1", viewModel.StepDisplay);
    } finally {
      File.Delete(path);
    }
  }

  [AvaloniaFact]
  public async Task SequenceTakeoffRejectsAssignmentChangedDuringConfirmation() {
    DateTime now = DateTime.UtcNow;
    FormationVehicleSource first = Source(new MAVLinkInterface(), 2, "first", now,
        PointAt(0, 5));
    FormationVehicleSource second = Source(new MAVLinkInterface(), 3, "second", now,
        PointAt(2, 5));
    string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
    try {
      SwarmSequenceFile.Save(path, new SwarmSequenceDocument {
        Layouts = [new SwarmSequenceLayout {
          Id = "L",
          Offset = new Dictionary<int, SwarmSequenceOffset> { [2] = new(0, 0, 5) },
        }],
      });
      var sink = new RecordingSink();
      SwarmSequenceViewModel? viewModel = null;
      viewModel = new SwarmSequenceViewModel(
          () => [first, second], sink,
          (_, _, _) => {
            viewModel!.Assignments[0].SelectedVehicle = viewModel.VehicleOptions[1];
            return Task.FromResult(true);
          });
      using (viewModel) {
        await viewModel.LoadAsync(path);

        await viewModel.TakeoffAssignedCommand.ExecuteAsync(null);

        Assert.Empty(sink.Arms);
        Assert.Empty(sink.Takeoffs);
        Assert.Contains("assignments changed", viewModel.Status,
            StringComparison.OrdinalIgnoreCase);
      }
    } finally {
      File.Delete(path);
    }
  }

  private static FollowLeaderPlan Plan(
      FormationVehicleSource ground, FormationVehicleSource air) => new(
      ground.Id, air.Id, [], new FollowLeaderSettings(5, 20, 10));

  private static FormationVehicleSource Source(
      MAVLinkInterface link,
      byte systemId,
      string endpoint,
      DateTime lastPacket,
      FollowPathPoint point,
      Firmwares firmware = Firmwares.ArduCopter2) {
    MAVState state = link.MAVlist[systemId, 1];
    state.lastvalidpacket = lastPacket;
    state.cs.lat = point.Latitude;
    state.cs.lng = point.Longitude;
    state.cs.alt = (float)point.Altitude;
    state.cs.firmware = firmware;
    return new FormationVehicleSource(
        new FormationVehicleId(link, systemId, 1), state, endpoint, IsOpen: true);
  }

  private static MAVLink.mavlink_mission_item_int_t MissionItem(int sequence, double eastM) {
    FollowPathPoint point = PointAt(eastM, 2);
    return new MAVLink.mavlink_mission_item_int_t {
      seq = (ushort)sequence,
      command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
      x = (int)Math.Round(point.Latitude * 1e7),
      y = (int)Math.Round(point.Longitude * 1e7),
      z = 2,
    };
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

  private static double EastFromStart(FollowPathPoint point) {
    (double distance, double bearing) = FormationGeometry.DistanceAndBearing(
        BaseLatitude, BaseLongitude, point.Latitude, point.Longitude);
    return distance * Math.Sin(bearing);
  }

  private static double NorthFromStart(FollowPathPoint point) {
    (double distance, double bearing) = FormationGeometry.DistanceAndBearing(
        BaseLatitude, BaseLongitude, point.Latitude, point.Longitude);
    return distance * Math.Cos(bearing);
  }

  private sealed class RecordingSink : IFollowLeaderCommandSink {
    internal List<IReadOnlyList<FormationVehicleSource>> StreamRequests { get; } = [];
    internal List<(FormationVehicleSource Vehicle, FollowPathPoint Target,
        FollowLeaderVelocity Velocity)> Targets { get; } = [];
    internal List<(FormationVehicleId Vehicle, bool Arm)> Arms { get; } = [];
    internal List<(FormationVehicleId Vehicle, string Mode)> Modes { get; } = [];
    internal List<(FormationVehicleId Vehicle, double Altitude)> Takeoffs { get; } = [];
    internal List<FormationVehicleId> NavGuided { get; } = [];

    public void RequestPositionStreams(IReadOnlyList<FormationVehicleSource> vehicles) =>
        StreamRequests.Add(vehicles.ToArray());

    public void SendPositionVelocity(
        FormationVehicleSource vehicle,
        FollowPathPoint target,
        FollowLeaderVelocity velocity) => Targets.Add((vehicle, target, velocity));

    public bool Arm(FormationVehicleSource vehicle, bool arm) {
      Arms.Add((vehicle.Id, arm));
      return true;
    }

    public void SetMode(FormationVehicleSource vehicle, string mode) =>
        Modes.Add((vehicle.Id, mode));

    public bool Takeoff(FormationVehicleSource vehicle, double altitudeM) {
      Takeoffs.Add((vehicle.Id, altitudeM));
      return true;
    }

    public bool EnableNavGuided(FormationVehicleSource vehicle) {
      NavGuided.Add(vehicle.Id);
      return true;
    }
  }
}
