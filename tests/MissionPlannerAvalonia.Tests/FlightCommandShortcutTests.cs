using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;
using MissionPlannerAvalonia.Views.GCSViews.ConfigurationView;

namespace MissionPlannerAvalonia.Tests;

public sealed class FlightCommandShortcutTests {
  [Fact]
  public async Task Confirmed_mode_command_uses_the_exact_captured_target() {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    NmeaVehicleTarget target = FreshTarget(now, armed: false, 42, 7);
    var sink = new RecordingSink();
    var confirmations = new List<string>();
    var statuses = new List<string>();
    var service = CreateService(
        () => target, sink,
        (title, text, accept) => {
          confirmations.Add(text);
          return Task.FromResult(true);
        },
        now, statuses: statuses);

    await service.ExecuteAsync(FlightCommandShortcut.Loiter);

    var call = Assert.Single(sink.Calls);
    Assert.Same(target, call.Target);
    Assert.Equal(FlightCommandShortcut.Loiter, call.Shortcut);
    Assert.Contains("vehicle 42:7", Assert.Single(confirmations));
    Assert.Contains("Alt+G", Assert.Single(statuses));
  }

  [Theory]
  [InlineData("Takeoff")]
  [InlineData("Land")]
  [InlineData("MinimumThrottle")]
  public async Task Hazardous_shortcuts_are_blocked_while_disarmed(
      string shortcutName) {
    FlightCommandShortcut shortcut = Enum.Parse<FlightCommandShortcut>(shortcutName);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    NmeaVehicleTarget target = FreshTarget(now, armed: false);
    var sink = new RecordingSink();
    var confirmations = new List<string>();
    var alerts = new List<string>();
    var service = CreateService(
        () => target, sink,
        (title, text, accept) => {
          confirmations.Add(text);
          return Task.FromResult(true);
        },
        now, alerts);

    await service.ExecuteAsync(shortcut);

    Assert.Empty(sink.Calls);
    Assert.Empty(confirmations);
    Assert.Contains("disarmed", Assert.Single(alerts), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Stale_telemetry_is_rejected_before_confirmation() {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    NmeaVehicleTarget target = FreshTarget(now, armed: true);
    target.Link.MAVlist[target.SystemId, target.ComponentId].lastvalidpacket =
        now.UtcDateTime - FlightCommandShortcuts.MaximumTelemetryAge - TimeSpan.FromMilliseconds(1);
    var sink = new RecordingSink();
    var alerts = new List<string>();
    bool confirmed = false;
    var service = CreateService(
        () => target, sink,
        (title, text, accept) => {
          confirmed = true;
          return Task.FromResult(true);
        },
        now, alerts);

    await service.ExecuteAsync(FlightCommandShortcut.Rtl);

    Assert.False(confirmed);
    Assert.Empty(sink.Calls);
    Assert.Contains("Recent telemetry", Assert.Single(alerts));
  }

  [Fact]
  public async Task Target_change_during_confirmation_prevents_command() {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    NmeaVehicleTarget first = FreshTarget(now, armed: true, 10, 1);
    NmeaVehicleTarget second = FreshTarget(now, armed: true, 11, 1);
    NmeaVehicleTarget selected = first;
    var sink = new RecordingSink();
    var alerts = new List<string>();
    var service = CreateService(
        () => selected, sink,
        (title, text, accept) => {
          selected = second;
          return Task.FromResult(true);
        },
        now, alerts);

    await service.ExecuteAsync(FlightCommandShortcut.Land);

    Assert.Empty(sink.Calls);
    Assert.Contains("changed", Assert.Single(alerts), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Reject_is_default_and_sink_validation_blocks_read_only_or_bad_modes() {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    NmeaVehicleTarget target = FreshTarget(now, armed: true);
    var sink = new RecordingSink { ValidationError = "The selected link is read-only." };
    var alerts = new List<string>();
    bool confirmed = false;
    var service = CreateService(
        () => target, sink,
        (title, text, accept) => {
          confirmed = true;
          return Task.FromResult(false);
        },
        now, alerts);

    await service.ExecuteAsync(FlightCommandShortcut.Auto);

    Assert.False(confirmed);
    Assert.Empty(sink.Calls);
    Assert.Contains("read-only", Assert.Single(alerts), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Explicit_confirmation_rejection_sends_nothing() {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    NmeaVehicleTarget target = FreshTarget(now, armed: true);
    var sink = new RecordingSink();
    var service = CreateService(
        () => target, sink,
        (title, text, accept) => Task.FromResult(false),
        now);

    await service.ExecuteAsync(FlightCommandShortcut.Takeoff);

    Assert.Empty(sink.Calls);
  }

  [Fact]
  public async Task Overlapping_key_repeat_is_ignored_until_the_first_command_finishes() {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    NmeaVehicleTarget target = FreshTarget(now, armed: true);
    var sink = new RecordingSink();
    var statuses = new List<string>();
    var confirmationOpened = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseConfirmation = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var service = CreateService(
        () => target, sink,
        (title, text, accept) => {
          confirmationOpened.TrySetResult();
          return releaseConfirmation.Task;
        },
        now, statuses: statuses);

    Task first = service.ExecuteAsync(FlightCommandShortcut.Auto);
    await confirmationOpened.Task;
    await service.ExecuteAsync(FlightCommandShortcut.Land);
    releaseConfirmation.SetResult(true);
    await first;

    Assert.Equal(FlightCommandShortcut.Auto, Assert.Single(sink.Calls).Shortcut);
    Assert.Contains(statuses, status => status.Contains(
        "another command shortcut", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void Official_noop_alt_f1_is_not_present_as_a_command() {
    FlightCommandShortcutInfo[] actions = Enum.GetValues<FlightCommandShortcut>()
        .Select(FlightCommandShortcuts.Describe).ToArray();

    Assert.Equal(8, actions.Length);
    Assert.DoesNotContain(actions, action => action.Gesture == "Alt+F1");
    Assert.Equal("Stabilize", FlightCommandShortcuts
        .Describe(FlightCommandShortcut.Stabilize).Mode);
  }

  [AvaloniaFact]
  public void Planner_settings_exposes_opt_in_and_visible_command_legend() {
    var view = new ConfigPlannerView {
      DataContext = new ConfigPlannerViewModel(),
    };

    Assert.NotNull(view.FindControl<CheckBox>("FlightCommandShortcutsToggle"));
    Assert.Contains(
        "Alt+0", view.FindControl<TextBlock>("FlightCommandShortcutsLegend")!.Text);
    (view.DataContext as IDisposable)?.Dispose();
  }

  private static FlightCommandShortcutService CreateService(
      Func<NmeaVehicleTarget?> capture,
      RecordingSink sink,
      Func<string, string, string, Task<bool>> confirm,
      DateTimeOffset now,
      List<string>? alerts = null,
      List<string>? statuses = null) =>
      new(
          capture,
          sink,
          confirm,
          (title, text) => {
            alerts?.Add(text);
            return Task.CompletedTask;
          },
          text => statuses?.Add(text),
          new FixedTimeProvider(now));

  private static NmeaVehicleTarget FreshTarget(
      DateTimeOffset now, bool armed, byte systemId = 1, byte componentId = 1) {
    var link = new MAVLinkInterface();
    MAVState state = link.MAVlist[systemId, componentId];
    state.lastvalidpacket = now.UtcDateTime;
    state.cs.armed = armed;
    state.cs.mode = "Stabilize";
    return new NmeaVehicleTarget(link, systemId, componentId);
  }

  private sealed class RecordingSink : IFlightCommandShortcutSink {
    internal string? ValidationError { get; init; }
    internal List<(NmeaVehicleTarget Target, FlightCommandShortcut Shortcut)> Calls { get; } = [];

    public string? Validate(
        NmeaVehicleTarget target, FlightCommandShortcut shortcut) => ValidationError;

    public Task<bool> ExecuteAsync(
        NmeaVehicleTarget target, FlightCommandShortcut shortcut) {
      Calls.Add((target, shortcut));
      return Task.FromResult(true);
    }
  }

  private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
  }
}
