using Avalonia.Headless.XUnit;
using MissionPlanner;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public sealed class ParameterRecoveryWorkflowTests {
  [Fact]
  public void Target_identity_includes_link_ids_and_exact_mav_state() {
    using var link = new MAVLinkInterface();
    link.sysidcurrent = 42;
    link.compidcurrent = 1;
    MAVState state = link.MAV;
    var target = new ParameterRecoveryTarget(link, state, 42, 1);

    Assert.True(ParameterRecoveryWorkflow.TargetsMatch(target, link));

    link.sysidcurrent = 43;
    Assert.False(ParameterRecoveryWorkflow.TargetsMatch(target, link));

    link.sysidcurrent = 42;
    using var replacement = new MAVState(link, 42, 1);
    var replacedStateTarget = target with { State = replacement };
    Assert.False(ParameterRecoveryWorkflow.TargetsMatch(replacedStateTarget, link));

    using var otherLink = new MAVLinkInterface();
    otherLink.sysidcurrent = 42;
    otherLink.compidcurrent = 1;
    Assert.False(ParameterRecoveryWorkflow.TargetsMatch(target, otherLink));
  }

  [Fact]
  public void Recovery_preserves_enable_first_id_reset_and_unchanged_rules() {
    using var link = new MAVLinkInterface();
    MAVState state = link.MAV;
    var target = new ParameterRecoveryTarget(link, state, state.sysid, state.compid);
    var operations = new List<string>();
    var values = new Dictionary<string, double> {
      ["B_ENABLE"] = 1,
      ["SENSOR_ID"] = 42,
      ["SAME"] = 5,
    };

    ParameterRecoveryResult result = ParameterRecoveryWorkflow.Run(
        values,
        target,
        _ => true,
        (_, name, response) => operations.Add($"read:{name}:{response}"),
        (_, name, value) => {
          operations.Add($"write:{name}:{value}");
          return true;
        },
        (_, name) => name == "SAME" ? 5 : null,
        CancellationToken.None);

    Assert.Equal(2, result.Set);
    Assert.Equal(1, result.Unchanged);
    Assert.Empty(result.Failed);
    Assert.Equal(new[] {
      "read:B_ENABLE:False",
      "read:SENSOR_ID:False",
      "read:SAME:False",
      "write:B_ENABLE:1",
      "read:B_ENABLE:True",
      "write:B_ENABLE:1",
      "read:SENSOR_ID:True",
      "write:SENSOR_ID:0",
      "write:SENSOR_ID:42",
    }, operations);
  }

  [Fact]
  public void Operator_cancellation_stops_before_the_next_write() {
    using var link = new MAVLinkInterface();
    MAVState state = link.MAV;
    var target = new ParameterRecoveryTarget(link, state, state.sysid, state.compid);
    using var cancellation = new CancellationTokenSource();
    int writes = 0;

    Assert.Throws<OperationCanceledException>(() => ParameterRecoveryWorkflow.Run(
        new Dictionary<string, double> { ["A_ENABLE"] = 1 },
        target,
        _ => true,
        (_, _, _) => { },
        (_, _, _) => {
          writes++;
          cancellation.Cancel();
          return true;
        },
        (_, _) => null,
        cancellation.Token));

    Assert.Equal(1, writes);
  }

  [Fact]
  public void Target_change_after_a_network_call_stops_without_writing() {
    using var link = new MAVLinkInterface();
    MAVState state = link.MAV;
    var target = new ParameterRecoveryTarget(link, state, state.sysid, state.compid);
    bool current = true;
    int writes = 0;

    Assert.Throws<ParameterRecoveryTargetChangedException>(() => ParameterRecoveryWorkflow.Run(
        new Dictionary<string, double> { ["SENSOR_ID"] = 42 },
        target,
        _ => current,
        (_, _, _) => current = false,
        (_, _, _) => {
          writes++;
          return true;
        },
        (_, _) => null,
        CancellationToken.None));

    Assert.Equal(0, writes);
  }

  [Fact]
  public void Rejected_value_is_reported_once() {
    using var link = new MAVLinkInterface();
    MAVState state = link.MAV;
    var target = new ParameterRecoveryTarget(link, state, state.sysid, state.compid);

    ParameterRecoveryResult result = ParameterRecoveryWorkflow.Run(
        new Dictionary<string, double> { ["TEST"] = 7 },
        target,
        _ => true,
        (_, _, _) => { },
        (_, _, _) => false,
        (_, _) => null,
        CancellationToken.None);

    Assert.Equal(0, result.Set);
    Assert.Equal(new[] { "TEST" }, result.Failed);
  }

  [AvaloniaFact]
  public void Developer_tools_exposes_restore_and_explicit_cancel_actions() {
    using var tools = new ConfigDeveloperToolsViewModel();

    Assert.Contains(tools.Actions, action => action.Label == "Restore Parameters (Recovery)");
    Assert.Contains(tools.Actions, action => action.Label == "Cancel Parameter Restore");
  }
}
