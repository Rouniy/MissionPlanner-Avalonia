using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Controls;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;
using MissionPlannerAvalonia.Views;

namespace MissionPlannerAvalonia.Tests;

public sealed class TrackerHomeModuleTests {
  private const string Gga =
      "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";

  [Fact]
  public async Task Tcp_client_ignores_non_gga_data_and_returns_valid_fix() {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    Task server = Task.Run(async () => {
      using TcpClient client = await listener.AcceptTcpClientAsync();
      await using NetworkStream stream = client.GetStream();
      byte[] data = Encoding.ASCII.GetBytes("$GPRMC,ignored*00\r\n" + Gga + "\r\n");
      await stream.WriteAsync(data);
    });
    var lines = new List<string>();
    var service = new TrackerHomeNmeaService();

    NmeaGgaFix fix = await service.ReadFixAsync(
        new TrackerHomeNmeaOptions(
            TrackerHomeNmeaTransport.TcpClient, "", 0, "127.0.0.1", port),
        lines.Add,
        CancellationToken.None);

    await server;
    listener.Stop();
    Assert.Equal(48.1173, fix.Latitude, 4);
    Assert.Equal(11.5166667, fix.Longitude, 4);
    Assert.Equal(2, lines.Count);
  }

  [Fact]
  public async Task Tcp_host_accepts_a_module_client_and_releases_the_port() {
    int port = FreeTcpPort();
    var service = new TrackerHomeNmeaService();
    Task<NmeaGgaFix> read = service.ReadFixAsync(
        new TrackerHomeNmeaOptions(
            TrackerHomeNmeaTransport.TcpHost, "", 0, "", port),
        null,
        CancellationToken.None);
    using TcpClient client = await ConnectWithRetryAsync(port);
    await client.GetStream().WriteAsync(
        Encoding.ASCII.GetBytes(Gga + "\n"), CancellationToken.None);

    NmeaGgaFix fix = await read;

    Assert.Equal(545.4, fix.AltitudeM, 2);
    var rebound = new TcpListener(IPAddress.Loopback, port);
    rebound.Start();
    rebound.Stop();
  }

  [Fact]
  public async Task Udp_listener_returns_one_fix_and_releases_the_socket() {
    int port = FreeUdpPort();
    var service = new TrackerHomeNmeaService();
    Task<NmeaGgaFix> read = service.ReadFixAsync(
        new TrackerHomeNmeaOptions(
            TrackerHomeNmeaTransport.UdpListener, "", 0, "", port),
        null,
        CancellationToken.None);
    using var sender = new UdpClient();
    byte[] datagram = Encoding.ASCII.GetBytes("noise\n" + Gga + "\r\n");
    await sender.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, port),
        CancellationToken.None);

    NmeaGgaFix fix = await read;

    Assert.Equal(1, fix.FixQuality);
    using var rebound = new UdpClient(port);
  }

  [Fact]
  public async Task Gpsd_mode_enables_raw_nmea_before_reading_fix() {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    string? watch = null;
    Task server = Task.Run(async () => {
      using TcpClient client = await listener.AcceptTcpClientAsync();
      using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
      watch = await reader.ReadLineAsync(CancellationToken.None);
      await client.GetStream().WriteAsync(
          Encoding.ASCII.GetBytes(Gga + "\n"), CancellationToken.None);
    });
    var service = new TrackerHomeNmeaService();

    NmeaGgaFix fix = await service.ReadFixAsync(
        new TrackerHomeNmeaOptions(
            TrackerHomeNmeaTransport.Gpsd, "", 0, "127.0.0.1", port),
        null,
        CancellationToken.None);

    await server;
    listener.Stop();
    Assert.Contains("\"nmea\":true", watch);
    Assert.Equal(8, fix.Satellites);
  }

  [Fact]
  public async Task Cancellation_stops_tcp_host_and_releases_port() {
    int port = FreeTcpPort();
    using var cts = new CancellationTokenSource();
    cts.CancelAfter(TimeSpan.FromMilliseconds(150));
    var service = new TrackerHomeNmeaService();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadFixAsync(
        new TrackerHomeNmeaOptions(
            TrackerHomeNmeaTransport.TcpHost, "", 0, "", port),
        null,
        cts.Token));

    var rebound = new TcpListener(IPAddress.Loopback, port);
    rebound.Start();
    rebound.Stop();
  }

  [Fact]
  public void Altitude_prefers_local_terrain_and_falls_back_to_gga_msl() {
    Assert.True(NmeaGgaParser.TryParse(Gga, out NmeaGgaFix fix, out string error), error);
    var local = new srtm.altresponce {
      currenttype = srtm.tiletype.valid,
      alt = 612.5,
      altsource = "GeoTIFF",
    };

    TrackerHomeAltitude resolved = TrackerHomeLocationResolver.Resolve(fix, local);
    TrackerHomeAltitude fallback = TrackerHomeLocationResolver.Resolve(
        fix, new srtm.altresponce { currenttype = srtm.tiletype.invalid });

    Assert.Equal(612.5, resolved.Metres);
    Assert.Equal("GeoTIFF", resolved.Source);
    Assert.False(resolved.UsedGpsFallback);
    Assert.Equal(545.4, fallback.Metres);
    Assert.True(fallback.UsedGpsFallback);
  }

  [Fact]
  public void Update_preserves_official_global_tracker_home_and_validates_position() {
    var firstLink = new MAVLinkInterface();
    var secondLink = new MAVLinkInterface();
    var target = new NmeaVehicleTarget(firstLink, 12, 1);

    FlightPlannerViewModel.SetTrackerHome(target, 35.125, 33.5, 84.25);

    PointLatLngAlt first = firstLink.MAVlist[12, 1].cs.TrackerLocation;
    PointLatLngAlt otherVehicle = firstLink.MAVlist[13, 1].cs.TrackerLocation;
    PointLatLngAlt otherModem = secondLink.MAVlist[12, 1].cs.TrackerLocation;
    Assert.Equal(35.125, first.Lat);
    Assert.Equal(33.5, first.Lng);
    Assert.Equal(84.25, first.Alt);
    Assert.Equal(first.Lat, otherVehicle.Lat);
    Assert.Equal(first.Lng, otherModem.Lng);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        FlightPlannerViewModel.SetTrackerHome(target, 91, 33.5, 84.25));
  }

  [AvaloniaFact]
  public void Native_dialog_and_official_tracker_home_submenu_are_present() {
    using var viewModel = new TrackerHomeModuleViewModel(
        new TrackerHomeNmeaService(), usePersistentSettings: false);
    var window = new TrackerHomeModuleWindow(viewModel);
    var planner = new FlightPlannerView();
    var map = planner.FindControl<FlightPlannerMap>("Map");

    Assert.NotNull(window.FindControl<Button>("ObtainTrackerHomeButton"));
    Assert.NotNull(window.FindControl<Button>("CancelTrackerHomeButton"));
    Assert.NotNull(map);
    MenuItem tracker = Assert.Single(
        map.ContextMenu!.Items.OfType<MenuItem>(),
        item => Equals(item.Header, "Tracker Home"));
    Assert.Equal(
        new[] { "Obtain From Module…", "Set Here…" },
        tracker.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray());
    window.Close();
  }

  private static int FreeTcpPort() {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
  }

  private static int FreeUdpPort() {
    using var udp = new UdpClient(0);
    return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
  }

  private static async Task<TcpClient> ConnectWithRetryAsync(int port) {
    Exception? last = null;
    for (int attempt = 0; attempt < 50; attempt++) {
      var client = new TcpClient();
      try {
        await client.ConnectAsync(
            IPAddress.Loopback, port, CancellationToken.None);
        return client;
      } catch (SocketException ex) {
        last = ex;
        client.Dispose();
        await Task.Delay(10);
      }
    }
    throw new InvalidOperationException("Tracker Home TCP host did not start.", last);
  }
}
