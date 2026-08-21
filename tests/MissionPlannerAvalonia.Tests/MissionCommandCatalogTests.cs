using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class MissionCommandCatalogTests {
  [Fact]
  public void Validate_accepts_known_and_custom_commands() {
    var waypoint = (ushort)MAVLink.MAV_CMD.WAYPOINT;
    MissionCommandCatalog.Validate(new[] {
      new MissionCommandDefinition(waypoint, ((MAVLink.MAV_CMD)waypoint).ToString(), new string[7]),
      new MissionCommandDefinition(60000, "VENDOR_SCAN", new string[7]),
    });
  }

  [Fact]
  public void Validate_rejects_duplicate_ids() {
    var definitions = new[] {
      new MissionCommandDefinition(60000, "ONE", new string[7]),
      new MissionCommandDefinition(60000, "TWO", new string[7]),
    };

    Assert.Throws<InvalidOperationException>(() => MissionCommandCatalog.Validate(definitions));
  }

  [Fact]
  public void Validate_requires_the_standard_name_for_known_id() {
    var definitions = new[] {
      new MissionCommandDefinition((ushort)MAVLink.MAV_CMD.WAYPOINT, "RENAMED", new string[7]),
    };

    Assert.Throws<InvalidOperationException>(() => MissionCommandCatalog.Validate(definitions));
  }

  [Fact]
  public void Validate_rejects_known_name_on_custom_id() {
    var definitions = new[] {
      new MissionCommandDefinition(60000, MAVLink.MAV_CMD.WAYPOINT.ToString(), new string[7]),
    };

    Assert.Throws<InvalidOperationException>(() => MissionCommandCatalog.Validate(definitions));
  }
}
