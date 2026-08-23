using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public class OpenDroneIdTests {
  private const string Gga =
      "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";

  [Fact]
  public void Gga_exposes_fix_quality_geoid_and_wgs84_altitude() {
    Assert.True(NmeaGgaParser.TryParse(Gga, out NmeaGgaFix fix, out string error), error);

    Assert.Equal(1, fix.FixQuality);
    Assert.Equal(46.9, fix.GeoidSeparationM);
    Assert.Equal(592.3, fix.GeodeticAltitudeM, 3);

    string withoutGeoid = WithChecksum(
        "$GNGGA,123519,4807.038,N,01131.000,E,2,08,0.9,545.4,M,,M,,");
    Assert.True(NmeaGgaParser.TryParse(
        withoutGeoid, out NmeaGgaFix unknown, out error), error);
    Assert.Null(unknown.GeoidSeparationM);
    Assert.Equal(OpenDroneIdMessageFactory.UnknownAltitudeM, unknown.GeodeticAltitudeM);
  }

  [Fact]
  public void Factory_encodes_all_official_identity_messages_and_broadcast_component() {
    OpenDroneIdConfiguration configuration = Configuration();
    Assert.True(OpenDroneIdMessageFactory.TryValidate(configuration, out string error), error);
    Assert.True(NmeaGgaParser.TryParse(Gga, out NmeaGgaFix fix, out error), error);
    var now = new DateTimeOffset(2020, 1, 1, 0, 0, 5, TimeSpan.Zero);

#pragma warning disable CS0612 // Current MAVLink Open Drone ID wire structs.
    MAVLink.mavlink_open_drone_id_basic_id_t basic =
        OpenDroneIdMessageFactory.BasicId(42, configuration);
    MAVLink.mavlink_open_drone_id_system_t system =
        OpenDroneIdMessageFactory.System(42, configuration, fix, now);
    MAVLink.mavlink_open_drone_id_self_id_t self =
        OpenDroneIdMessageFactory.SelfId(42, configuration);
    MAVLink.mavlink_open_drone_id_operator_id_t operatorId =
        OpenDroneIdMessageFactory.OperatorId(42, configuration);
#pragma warning restore CS0612
    MAVLink.mavlink_open_drone_id_system_update_t update =
        OpenDroneIdMessageFactory.SystemUpdate(42, fix, now);

    Assert.Equal(42, basic.target_system);
    Assert.Equal((byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_ALL, basic.target_component);
    Assert.All(basic.id_or_mac, value => Assert.Equal(0, value));
    Assert.Equal("ABC123", FixedText(basic.uas_id));
    Assert.Equal((byte)MAVLink.MAV_ODID_ID_TYPE.SERIAL_NUMBER, basic.id_type);
    Assert.Equal((byte)MAVLink.MAV_ODID_UA_TYPE.HELICOPTER_OR_MULTIROTOR, basic.ua_type);

    Assert.Equal(481173000, system.operator_latitude);
    Assert.Equal(115166667, system.operator_longitude);
    Assert.Equal(592.3f, system.operator_altitude_geo, 2);
    Assert.Equal((ushort)2, system.area_count);
    Assert.Equal((ushort)25, system.area_radius);
    Assert.Equal(42, update.target_system);
    Assert.Equal(481173000, update.operator_latitude);
    Assert.Equal(592.3f, update.operator_altitude_geo, 2);
    Assert.Equal(OpenDroneIdMessageFactory.Timestamp2019(now), update.timestamp);
    Assert.Equal("Survey flight", FixedText(self.description));
    Assert.Equal("OP-987", FixedText(operatorId.operator_id));
  }

  [Fact]
  public void Factory_uses_protocol_unknowns_for_stale_position_and_rejects_bad_identity() {
    OpenDroneIdConfiguration configuration = Configuration();
    var now = DateTimeOffset.UtcNow;
#pragma warning disable CS0612 // Current MAVLink Open Drone ID wire struct.
    MAVLink.mavlink_open_drone_id_system_t stale =
        OpenDroneIdMessageFactory.System(7, configuration, null, now);
#pragma warning restore CS0612

    Assert.Equal(0, stale.operator_latitude);
    Assert.Equal(0, stale.operator_longitude);
    Assert.Equal(OpenDroneIdMessageFactory.UnknownAltitudeM, stale.operator_altitude_geo);

    Assert.False(OpenDroneIdMessageFactory.TryValidate(
        configuration with { UasId = "кириллица" }, out string asciiError));
    Assert.Contains("ASCII", asciiError);
    Assert.False(OpenDroneIdMessageFactory.TryValidate(
        configuration with { Description = new string('x', 24) }, out string lengthError));
    Assert.Contains("23", lengthError);
    Assert.False(OpenDroneIdMessageFactory.TryValidate(
        configuration with { AreaFloorM = 500, AreaCeilingM = 100 }, out string floorError));
    Assert.Contains("floor", floorError, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Scheduler_matches_official_rates_and_four_message_rotation() {
    var scheduler = new OpenDroneIdSendScheduler();
    var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    Assert.Null(scheduler.Next(start, moduleDetected: false, freshGps: true));
    Assert.True(scheduler.Next(start, moduleDetected: true, freshGps: true)!.Value.SystemUpdate);
    OpenDroneIdScheduledMessage basic =
        scheduler.Next(start.AddMilliseconds(100), moduleDetected: true, freshGps: true)!.Value;
    Assert.False(basic.SystemUpdate);
    Assert.Equal(OpenDroneIdExtendedMessage.BasicId, basic.ExtendedKind);
    Assert.Null(scheduler.Next(start.AddMilliseconds(200), moduleDetected: true, freshGps: true));
    Assert.True(scheduler.Next(start.AddSeconds(1), moduleDetected: true, freshGps: true)!.Value.SystemUpdate);

    OpenDroneIdScheduledMessage system =
        scheduler.Next(start.AddSeconds(2.6), moduleDetected: true, freshGps: false)!.Value;
    Assert.Equal(OpenDroneIdExtendedMessage.System, system.ExtendedKind);
    OpenDroneIdScheduledMessage self =
        scheduler.Next(start.AddSeconds(5.2), moduleDetected: true, freshGps: false)!.Value;
    Assert.Equal(OpenDroneIdExtendedMessage.SelfId, self.ExtendedKind);
    OpenDroneIdScheduledMessage operatorId =
        scheduler.Next(start.AddSeconds(7.8), moduleDetected: true, freshGps: false)!.Value;
    Assert.Equal(OpenDroneIdExtendedMessage.OperatorId, operatorId.ExtendedKind);
    Assert.Equal(TimeSpan.FromSeconds(10),
        OpenDroneIdSendScheduler.ExtendedInterval * 4);
  }

  [AvaloniaFact]
  public async Task Session_rejects_transmission_without_explicit_confirmation() {
    MAVLinkInterface link = OpenLink();
    NmeaVehicleTarget? current = new(link, 4, 1);
    var adapter = new FakeAdapter();
    int opens = 0;
    using var viewModel = new OpenDroneIdViewModel(
        _ => current,
        adapter,
        _ => Task.FromResult(false),
        (_, _, _, _) => {
          opens++;
          return (new RepeatingLineSerial(Gga), null);
        });
    viewModel.SelectedInput = OpenDroneIdViewModel.UdpHost;

    await viewModel.ToggleCommand.ExecuteAsync(null);

    Assert.False(viewModel.Running);
    Assert.Equal(0, opens);
    Assert.Empty(adapter.Components);
    Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public async Task Session_waits_for_module_and_stops_sends_and_subscriptions_on_target_switch() {
    MAVLinkInterface firstLink = OpenLink();
    MAVLinkInterface secondLink = OpenLink();
    NmeaVehicleTarget? current = new(firstLink, 12, 1);
    var adapter = new FakeAdapter();
    var gps = new RepeatingLineSerial(Gga);
    int confirmations = 0;
    using var viewModel = new OpenDroneIdViewModel(
        _ => current,
        adapter,
        _ => {
          confirmations++;
          return Task.FromResult(true);
        },
        (_, _, _, _) => (gps, null));
    viewModel.SelectedInput = OpenDroneIdViewModel.UdpHost;
    viewModel.UasId = "ABC123";
    viewModel.OperatorId = "OP-987";

    await viewModel.ToggleCommand.ExecuteAsync(null);

    Assert.True(viewModel.Running, viewModel.Status);
    Assert.Equal(1, confirmations);
    Assert.Equal(new byte[] { 1, 236, 237, 238 }, adapter.Components.OrderBy(value => value));
    await Task.Delay(300);
    Assert.Empty(adapter.Sent);

    adapter.Busy = true;
    adapter.EmitGood(236);
    await Task.Delay(300);
    Assert.Empty(adapter.Sent);
    adapter.Busy = false;
    await WaitUntilAsync(() => !adapter.Sent.IsEmpty);
    Assert.All(adapter.Targets, target => {
      Assert.Same(firstLink, target.Link);
      Assert.Equal(12, target.SystemId);
      Assert.Equal(1, target.ComponentId);
    });

    current = new NmeaVehicleTarget(secondLink, 12, 1);
    viewModel.SynchronizeActiveTarget();
    await WaitUntilAsync(() => !viewModel.Running
        && viewModel.Status.Contains("changed or disconnected", StringComparison.OrdinalIgnoreCase));
    int sentAfterStop = adapter.Sent.Count;
    await Task.Delay(350);

    Assert.Equal(sentAfterStop, adapter.Sent.Count);
    Assert.Equal(4, adapter.Unsubscribed.Count);
    Assert.True(gps.WasClosed);
  }

  [AvaloniaFact]
  public void Native_flight_data_exposes_official_drone_id_tab_and_map_status() {
    using var viewModel = new FlightDataViewModel();
    var view = new FlightDataView { DataContext = viewModel };
    var droneView = new OpenDroneIdView { DataContext = viewModel.OpenDroneId };

    Assert.NotNull(view.FindControl<TabItem>("tabDroneId"));
    Assert.NotNull(droneView.FindControl<Button>("ToggleOpenDroneIdButton"));
    Assert.True(FlightDataView.ProfileAllowsTab("Drone ID", new DisplayView()));
  }

  private static OpenDroneIdConfiguration Configuration() => new(
      MAVLink.MAV_ODID_ID_TYPE.SERIAL_NUMBER,
      "ABC123",
      MAVLink.MAV_ODID_UA_TYPE.HELICOPTER_OR_MULTIROTOR,
      MAVLink.MAV_ODID_DESC_TYPE.TEXT,
      "Survey flight",
      2,
      25,
      300,
      10,
      MAVLink.MAV_ODID_CATEGORY_EU.OPEN,
      MAVLink.MAV_ODID_CLASS_EU.CLASS_2,
      MAVLink.MAV_ODID_CLASSIFICATION_TYPE.EU,
      MAVLink.MAV_ODID_OPERATOR_LOCATION_TYPE.LIVE_GNSS,
      MAVLink.MAV_ODID_OPERATOR_ID_TYPE.CAA,
      "OP-987");

  private static string FixedText(byte[] value) =>
      Encoding.ASCII.GetString(value).TrimEnd('\0');

  private static string WithChecksum(string body) =>
      body + "*" + NmeaGgaParser.Checksum(body);

  private static MAVLinkInterface OpenLink() {
    var link = new MAVLinkInterface { BaseStream = new RepeatingLineSerial("") };
    return link;
  }

  private static async Task WaitUntilAsync(Func<bool> condition) {
    for (int attempt = 0; attempt < 300; attempt++) {
      Dispatcher.UIThread.RunJobs();
      if (condition()) {
        return;
      }
      await Task.Delay(10);
    }
    Assert.Fail("Condition was not reached before the test timeout.");
  }

  private sealed class FakeAdapter : IOpenDroneIdMavlinkAdapter {
    private readonly ConcurrentDictionary<byte, Func<byte, string, bool>> _handlers = new();
    internal ConcurrentQueue<object> Sent { get; } = new();
    internal ConcurrentQueue<NmeaVehicleTarget> Targets { get; } = new();
    internal ConcurrentQueue<int> Unsubscribed { get; } = new();
    internal IEnumerable<byte> Components => _handlers.Keys;
    internal bool Busy { get; set; }

    public bool IsBusy(NmeaVehicleTarget target) => Busy;

    public int SubscribeArmStatus(
        NmeaVehicleTarget target, byte componentId, Func<byte, string, bool> handler) {
      _handlers[componentId] = handler;
      return componentId;
    }

    public void Unsubscribe(NmeaVehicleTarget target, int subscription) =>
        Unsubscribed.Enqueue(subscription);

    public void Send(NmeaVehicleTarget target, object packet) {
      Targets.Enqueue(target);
      Sent.Enqueue(packet);
    }

    internal void EmitGood(byte component) =>
        _handlers[component]((byte)MAVLink.MAV_ODID_ARM_STATUS.GOOD_TO_ARM, "");
  }

  private sealed class RepeatingLineSerial : ICommsSerial {
    private readonly string _line;
    private int _open = 1;

    internal RepeatingLineSerial(string line) => _line = line;
    internal bool WasClosed { get; private set; }
    public Stream BaseStream { get; } = new MemoryStream();
    public int BaudRate { get; set; }
    public int BytesToRead => 0;
    public int BytesToWrite => 0;
    public int DataBits { get; set; } = 8;
    public bool DtrEnable { get; set; }
    public bool IsOpen => Volatile.Read(ref _open) != 0;
    public string PortName { get; set; } = "TEST";
    public int ReadBufferSize { get; set; }
    public int ReadTimeout { get; set; }
    public bool RtsEnable { get; set; }
    public int WriteBufferSize { get; set; }
    public int WriteTimeout { get; set; }
    public void Open() => Volatile.Write(ref _open, 1);
    public void Close() {
      WasClosed = true;
      Volatile.Write(ref _open, 0);
    }
    public string ReadLine() {
      if (!IsOpen) {
        throw new IOException("Input is closed.");
      }
      Thread.Sleep(5);
      return _line;
    }
    public void Dispose() {
      Close();
      BaseStream.Dispose();
    }
    public void DiscardInBuffer() { }
    public int Read(byte[] buffer, int offset, int count) => 0;
    public int ReadByte() => -1;
    public int ReadChar() => -1;
    public string ReadExisting() => "";
    public void Write(string text) { }
    public void Write(byte[] buffer, int offset, int count) { }
    public void WriteLine(string text) { }
    public void toggleDTR() { }
  }
}
