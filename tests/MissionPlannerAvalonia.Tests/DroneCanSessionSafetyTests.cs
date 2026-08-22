using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public class DroneCanSessionSafetyTests {
  [Fact]
  public void Results_require_the_same_modem_vehicle_and_monotonic_revisions() {
    var firstLink = new MissionPlanner.MAVLinkInterface();
    var secondLink = new MissionPlanner.MAVLinkInterface();
    var expected = new DroneCanSessionTarget(firstLink, 1, 1);

    Assert.True(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(secondLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(firstLink, 2, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        true, 7, 7, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 8, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 6));
  }

  [AvaloniaFact]
  public void Target_switch_immediately_clears_nodes_parameters_selection_and_logs() {
    var firstLink = new MissionPlanner.MAVLinkInterface();
    var secondLink = new MissionPlanner.MAVLinkInterface();
    DroneCanSessionTarget? current = new(firstLink, 1, 1);
    using var viewModel = new ConfigDroneCanViewModel(() => current);
    var node = new DroneCanNode { Id = 42, Name = "old-node" };
    viewModel.Nodes.Add(node);
    viewModel.SelectedNode = node;
    viewModel.NodeParams.Add(new DroneCanParam { Name = "OLD_PARAM", Value = "1" });
    viewModel.DebugLog.Add(new DroneCanLog { Node = "42", Text = "old-message" });

    current = new DroneCanSessionTarget(secondLink, 1, 1);
    viewModel.SynchronizeActiveTarget();
    Dispatcher.UIThread.RunJobs();

    Assert.Empty(viewModel.Nodes);
    Assert.Empty(viewModel.NodeParams);
    Assert.Empty(viewModel.DebugLog);
    Assert.Null(viewModel.SelectedNode);
    Assert.False(viewModel.IsConnected);
    Assert.Contains("active modem or vehicle changed", viewModel.Status,
        StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public void Native_view_locks_interface_selection_while_a_session_or_operation_is_active() {
    using var viewModel = new ConfigDroneCanViewModel(() => null);
    var view = new ConfigDroneCanView { DataContext = viewModel };

    Assert.NotNull(view.FindControl<ComboBox>("DroneCanInterfaceSelector"));
    Assert.NotNull(view.FindControl<Button>("DroneCanConnectButton"));
    Assert.True(viewModel.CanChangeInterface);
    Assert.True(viewModel.CanToggleConnection);
  }
}
