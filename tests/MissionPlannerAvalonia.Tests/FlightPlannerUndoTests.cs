using Avalonia.Headless.XUnit;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class FlightPlannerUndoTests {
  [AvaloniaFact]
  public void Undo_restores_each_mission_addition() {
    var vm = NewViewModel();

    vm.AddWaypointAt(34.1, 33.1);
    vm.AddWaypointAt(34.2, 33.2);

    vm.UndoCommand.Execute(null);
    var remaining = Assert.Single(vm.Waypoints);
    Assert.Equal(34.1, remaining.Lat, 6);
    Assert.True(vm.CanUndo);

    vm.UndoCommand.Execute(null);
    Assert.Empty(vm.Waypoints);
    Assert.False(vm.CanUndo);
  }

  [AvaloniaFact]
  public void Undo_captures_direct_grid_edits_before_the_value_changes() {
    var vm = NewViewModel();
    vm.AddWaypointAt(34.1, 33.1);
    var row = Assert.Single(vm.Waypoints);
    double original = row.Alt;

    row.Alt = original + 25;
    vm.UndoCommand.Execute(null);

    Assert.Equal(original, Assert.Single(vm.Waypoints).Alt, 6);
  }

  [AvaloniaFact]
  public void Undo_restores_all_mission_type_stores() {
    var vm = NewViewModel();
    vm.AddWaypointAt(34.1, 33.1);
    vm.MissionType = "Fence";
    vm.AddWaypointAt(34.2, 33.2);

    vm.UndoCommand.Execute(null);

    Assert.Equal("Fence", vm.MissionType);
    Assert.Empty(vm.Waypoints);
    vm.MissionType = "Mission";
    Assert.Single(vm.Waypoints);

    vm.UndoCommand.Execute(null);
    Assert.Equal("Mission", vm.MissionType);
    Assert.Empty(vm.Waypoints);
  }

  [AvaloniaFact]
  public void Undo_restores_home_location() {
    var vm = NewViewModel();
    vm.HomeLat = 34;
    vm.HomeLng = 33;
    vm.HomeAlt = 10;

    vm.SetHome(35, 34);
    vm.UndoCommand.Execute(null);

    Assert.Equal(34, vm.HomeLat, 6);
    Assert.Equal(33, vm.HomeLng, 6);
    Assert.Equal(10, vm.HomeAlt, 6);
  }

  private static FlightPlannerViewModel NewViewModel() => new() { VerifyHeight = false };
}
