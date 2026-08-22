using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class ConnectionListTests {
  [Fact]
  public void Parser_accepts_all_official_connection_list_transports() {
    ConnectionListParseResult result = ConnectionListParser.Parse([
      "tcp://modem.local:5760",
      "udp://0.0.0.0:14550",
      "udpcl://192.0.2.20:14551",
      "serial:/dev/ttyUSB0:115200",
    ]);

    Assert.Empty(result.Errors);
    Assert.Collection(result.Endpoints,
        endpoint => {
          Assert.Equal(ConnectionListTransport.TcpClient, endpoint.Transport);
          Assert.Equal("tcp://modem.local:5760", endpoint.Canonical);
        },
        endpoint => {
          Assert.Equal(ConnectionListTransport.UdpListener, endpoint.Transport);
          Assert.Equal(14550, endpoint.Port);
        },
        endpoint => {
          Assert.Equal(ConnectionListTransport.UdpClient, endpoint.Transport);
          Assert.Equal("192.0.2.20", endpoint.Host);
        },
        endpoint => {
          Assert.Equal(ConnectionListTransport.Serial, endpoint.Transport);
          Assert.Equal("/dev/ttyUSB0", endpoint.SerialPort);
          Assert.Equal(115200, endpoint.BaudRate);
        });
  }

  [Fact]
  public void Parser_supports_comments_ipv6_and_reports_bad_or_duplicate_rows() {
    ConnectionListParseResult result = ConnectionListParser.Parse([
      "# field modems",
      "tcp://[2001:db8::5]:5760",
      "TCP://[2001:DB8::5]:5760",
      "udp://localhost:0",
      "not-a-connection",
      "",
    ]);

    ConnectionListEndpoint endpoint = Assert.Single(result.Endpoints);
    Assert.Equal("tcp://[2001:db8::5]:5760", endpoint.Canonical);
    Assert.Collection(result.Errors,
        error => {
          Assert.Equal(3, error.Line);
          Assert.Contains("Duplicate", error.Message);
        },
        error => {
          Assert.Equal(4, error.Line);
          Assert.Contains("between 1 and 65535", error.Message);
        },
        error => {
          Assert.Equal(5, error.Line);
          Assert.Contains("Expected", error.Message);
        });
  }

  [Theory]
  [InlineData("tcp://host:65536")]
  [InlineData("udpcl://:14550")]
  [InlineData("serial:/dev/ttyUSB0:0")]
  [InlineData("ws://host:8080")]
  public void Parser_never_silently_accepts_an_invalid_row(string row) {
    ConnectionListParseResult result = ConnectionListParser.Parse([row]);

    Assert.Empty(result.Endpoints);
    Assert.Single(result.Errors);
  }

  [Fact]
  public async Task Registry_switches_between_distinct_links_and_falls_back_after_remove() {
    using MAVLinkInterface primary = OpenInterface();
    using MAVLinkInterface secondary = OpenInterface();
    using var manager = new MavLinkConnectionManager(primary);
    var endpoint = new ConnectionListEndpoint(
        ConnectionListTransport.UdpListener, "0.0.0.0", 14550, "", 0, 1);
    MavLinkConnection added = manager.Add(secondary, endpoint);
    int activeChanges = 0;
    manager.ActiveChanged += (_, _) => activeChanges++;

    Assert.True(manager.SetActive(secondary));
    Assert.Same(secondary, manager.Active.Link);

    Assert.True(await manager.RemoveAsync(added, close: false));
    Assert.Same(primary, manager.Active.Link);
    Assert.Equal(2, activeChanges);
    Assert.Single(manager.Snapshot());
  }

  [Fact]
  public void Registry_rejects_duplicate_endpoint_without_replacing_live_link() {
    using MAVLinkInterface primary = OpenInterface();
    using MAVLinkInterface first = OpenInterface();
    using MAVLinkInterface duplicate = OpenInterface();
    using var manager = new MavLinkConnectionManager(primary);
    var endpoint = new ConnectionListEndpoint(
        ConnectionListTransport.TcpClient, "MODEM.local", 5760, "", 0, 1);
    manager.Add(first, endpoint);

    var error = Assert.Throws<InvalidOperationException>(() => manager.Add(
        duplicate, endpoint with { Host = "modem.LOCAL", SourceLine = 2 }));

    Assert.Contains("already exists", error.Message);
    Assert.Equal(2, manager.Snapshot().Count);
    Assert.Same(first, manager.Snapshot()[1].Link);
  }

  [Fact]
  public void Connection_entries_build_the_expected_upstream_transport() {
    var cases = new[] {
      (ConnectionListTransport.TcpClient, "host", 5760, "", 0,
          Expected: typeof(PreconfiguredTcpSerial)),
      (ConnectionListTransport.UdpClient, "host", 14550, "", 0,
          Expected: typeof(PreconfiguredUdpClient)),
      (ConnectionListTransport.UdpListener, "0.0.0.0", 14550, "", 0,
          Expected: typeof(UdpSerial)),
      (ConnectionListTransport.Serial, "", 0, "/dev/ttyUSB0", 115200,
          Expected: typeof(SerialPort)),
    };

    foreach (var test in cases) {
      var endpoint = new ConnectionListEndpoint(
          test.Item1, test.Item2, test.Item3, test.Item4, test.Item5, 1);
      ICommsSerial stream = ConnectionListService.CreateStream(endpoint);
      try {
        Assert.IsType(test.Expected, stream);
        if (stream is SerialPort serial) {
          Assert.Equal(test.Item4, serial.PortName);
          Assert.Equal(test.Item5, serial.BaudRate);
        }
      } finally {
        stream.Close();
        (stream as IDisposable)?.Dispose();
      }
    }
  }

  [Fact]
  public void Preconfigured_udp_clients_keep_their_own_endpoint() {
    using var firstReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var secondReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    int firstPort = ((IPEndPoint)firstReceiver.Client.LocalEndPoint!).Port;
    int secondPort = ((IPEndPoint)secondReceiver.Client.LocalEndPoint!).Port;
    using var first = new PreconfiguredUdpClient("127.0.0.1", firstPort.ToString());
    using var second = new PreconfiguredUdpClient("localhost", secondPort.ToString());

    first.Open();
    second.Open();

    Assert.Equal(firstPort, first.hostEndPoint.Port);
    Assert.Equal(secondPort, second.hostEndPoint.Port);
    Assert.Equal(IPAddress.Loopback, first.hostEndPoint.Address);
  }

  [Fact]
  public async Task Opening_an_unresponsive_udp_list_is_promptly_cancellable() {
    int port = ReserveUdpPort();
    string path = Path.Combine(
        Path.GetTempPath(), $"mp-connection-list-{Guid.NewGuid():N}.txt");
    await File.WriteAllTextAsync(path, $"udp://127.0.0.1:{port}\n");
    using MAVLinkInterface primary = OpenInterface();
    using var manager = new MavLinkConnectionManager(primary);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    var stopwatch = Stopwatch.StartNew();

    try {
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
          ConnectionListService.OpenFileAsync(path, manager, cancellation.Token));
    } finally {
      File.Delete(path);
    }

    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
        $"Cancellation took {stopwatch.Elapsed}.");
    Assert.Single(manager.Snapshot());
  }

  [Fact]
  public async Task Secondary_runtime_reads_real_udp_telemetry_for_its_own_vehicle() {
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
    using var sender = new UdpClient();
    sender.Connect(IPAddress.Loopback, port);
    using var sendCancellation = new CancellationTokenSource();
    int latitude = 351000000;
    Task sendLoop = SendVehiclePacketsAsync(
        sender, systemId: 42, () => Volatile.Read(ref latitude), sendCancellation.Token);
    var secondary = new MAVLinkInterface {
      BaseStream = new UdpSerial(receiver),
      CONNECT_TIMEOUT_SECONDS = 3,
    };
    using MAVLinkInterface primary = OpenInterface();
    using var manager = new MavLinkConnectionManager(primary);
    MavLinkConnection? connection = null;

    try {
      await Task.Run(() => secondary.Open(
          getparams: false, skipconnectedcheck: true, showui: false))
          .WaitAsync(TimeSpan.FromSeconds(8));
      Assert.Equal(42, secondary.sysidcurrent);
      Assert.Equal(1, secondary.compidcurrent);

      connection = manager.Add(secondary,
          new ConnectionListEndpoint(
              ConnectionListTransport.UdpListener, "127.0.0.1", port, "", 0, 1),
          item => new MavLinkSecondaryRuntime(
              item, manager.NotifyClosed, TimeSpan.FromSeconds(2)));
      Assert.True(manager.SetActive(connection));

      Volatile.Write(ref latitude, 352345678);
      await WaitUntilAsync(() => {
        MAVLink.MAVLinkMessage? packet = secondary.MAVlist[42, 1]
            .getPacketLast((uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT);
        return packet?.ToStructure<MAVLink.mavlink_global_position_int_t>().lat == 352345678;
      }, TimeSpan.FromSeconds(3));

      Assert.Same(secondary, manager.Active.Link);
      Assert.True(secondary.MAVlist[42, 1].lastvalidpacket > DateTime.MinValue);
    } finally {
      sendCancellation.Cancel();
      try {
        await sendLoop;
      } catch (OperationCanceledException) {
      }
      if (connection != null && manager.Find(secondary) != null) {
        await manager.RemoveAsync(connection);
      } else {
        secondary.Dispose();
      }
    }
  }

  [Fact]
  public async Task Connection_list_opens_two_real_udp_vehicles_as_independent_links() {
    int firstPort = ReserveUdpPort();
    int secondPort;
    do {
      secondPort = ReserveUdpPort();
    } while (secondPort == firstPort);
    string path = Path.Combine(
        Path.GetTempPath(), $"mp-connection-list-{Guid.NewGuid():N}.txt");
    await File.WriteAllLinesAsync(path, [
      $"udp://127.0.0.1:{firstPort}",
      $"udp://127.0.0.1:{secondPort}",
    ]);
    using var firstSender = new UdpClient();
    using var secondSender = new UdpClient();
    firstSender.Connect(IPAddress.Loopback, firstPort);
    secondSender.Connect(IPAddress.Loopback, secondPort);
    using var sendCancellation = new CancellationTokenSource();
    Task firstLoop = SendVehiclePacketsAsync(
        firstSender, 41, () => 351000000, sendCancellation.Token);
    Task secondLoop = SendVehiclePacketsAsync(
        secondSender, 42, () => 352000000, sendCancellation.Token);
    using MAVLinkInterface primary = OpenInterface();
    using var manager = new MavLinkConnectionManager(primary);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

    try {
      ConnectionListOpenResult result = await ConnectionListService.OpenFileAsync(
          path, manager, timeout.Token, openTelemetryLogs: false);

      Assert.Empty(result.ParseErrors);
      Assert.Empty(result.Failures);
      Assert.Equal(2, result.Opened.Count);
      Assert.Equal(3, manager.Snapshot().Count);
      Assert.Equal(new byte[] { 41, 42 }, result.Opened
          .Select(connection => connection.Link.MAV.sysid)
          .Order().ToArray());
      Assert.NotSame(result.Opened[0].Link, result.Opened[1].Link);
    } finally {
      File.Delete(path);
      sendCancellation.Cancel();
      foreach (Task loop in new[] { firstLoop, secondLoop }) {
        try {
          await loop;
        } catch (OperationCanceledException) {
        }
      }
    }
  }

  private static async Task SendVehiclePacketsAsync(
      UdpClient sender,
      byte systemId,
      Func<int> latitude,
      CancellationToken cancellationToken) {
    var parser = new MAVLink.MavlinkParse();
    int sequence = 0;
    while (!cancellationToken.IsCancellationRequested) {
      byte[] heartbeat = parser.GenerateMAVLinkPacket20(
          MAVLink.MAVLINK_MSG_ID.HEARTBEAT,
          new MAVLink.mavlink_heartbeat_t(
              0,
              (byte)MAVLink.MAV_TYPE.QUADROTOR,
              (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
              0,
              (byte)MAVLink.MAV_STATE.ACTIVE,
              3),
          false, systemId, 1, sequence++);
      byte[] position = parser.GenerateMAVLinkPacket20(
          MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT,
          new MAVLink.mavlink_global_position_int_t(
              (uint)Environment.TickCount64,
              latitude(),
              332000000,
              100000,
              100000,
              100,
              0,
              0,
              9000),
          false, systemId, 1, sequence++);
      try {
        await sender.SendAsync(heartbeat, cancellationToken);
        await sender.SendAsync(position, cancellationToken);
      } catch (SocketException) when (!cancellationToken.IsCancellationRequested) {
        // A connected UDP socket can surface ICMP "port unreachable" while the tested listener is
        // still being created. The next datagram is valid once Connection List binds the port.
      }
      await Task.Delay(40, cancellationToken);
    }
  }

  private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout) {
    DateTime deadline = DateTime.UtcNow + timeout;
    while (!condition()) {
      if (DateTime.UtcNow >= deadline) {
        throw new TimeoutException("Condition was not met before the test deadline.");
      }
      await Task.Delay(20);
    }
  }

  private static int ReserveUdpPort() {
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
  }

  private static MAVLinkInterface OpenInterface() => new() {
    BaseStream = new UdpSerial(new UdpClient(0)),
  };
}
