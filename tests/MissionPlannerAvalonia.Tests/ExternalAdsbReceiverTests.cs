using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using MissionPlanner.Utilities;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class ExternalAdsbReceiverTests {
  [Fact]
  public void Options_preserve_upstream_defaults_and_normalize_a_trailing_slash() {
    var defaults = new ExternalAdsbOptions(true, "https://api.adsb.lol/", 0);
    var tcp = new ExternalAdsbOptions(true, " receiver.local ", 31004);

    Assert.Equal(ExternalAdsbOptions.DefaultServer, defaults.Server);
    Assert.Equal(ExternalAdsbOptions.DefaultPort, defaults.Port);
    Assert.True(defaults.IsHttp);
    Assert.Equal("receiver.local", tcp.Server);
    Assert.Equal(31004, tcp.Port);
    Assert.False(tcp.IsHttp);
    Assert.Equal("https://api.adsb.lol/v2/point/34.5/33.25/100",
        ExternalAdsbReceiver.BuildHttpUrl(defaults.Server, 34.5, 33.25, 100));
    Assert.Equal(110, ExternalAdsbReceiver.NextHttpRadius(100, 9));
    Assert.Equal(250, ExternalAdsbReceiver.NextHttpRadius(250, 0));
    Assert.Equal(100, ExternalAdsbReceiver.NextHttpRadius(100, 10));
    Assert.Equal(2000, ExternalAdsbReceiver.NextHttpDelay(1000));
    Assert.Equal(10_000, ExternalAdsbReceiver.NextHttpDelay(8000));
  }

  [Fact]
  public void Sbs_decoder_merges_callsign_and_velocity_before_emitting_position() {
    var decoder = new ExternalAdsbDecoder();

    Assert.False(decoder.TryDecodeLine(Sbs("1", "ABC123", fields => {
      fields[10] = "TEST123 ";
    }), out _));
    Assert.False(decoder.TryDecodeLine(Sbs("4", "ABC123", fields => {
      fields[12] = "250";
      fields[13] = "123.4";
    }), out _));
    Assert.True(decoder.TryDecodeLine(Sbs("3", "ABC123", fields => {
      fields[11] = "10000";
      fields[14] = "34.5";
      fields[15] = "33.25";
      fields[16] = "600";
      fields[17] = "7700";
    }), out var plane));

    Assert.Equal("ABC123", plane.Tag);
    Assert.Equal("TEST123", plane.CallSign);
    Assert.Equal(3048, plane.Alt, 3);
    Assert.Equal(123.4f, plane.Heading, 3);
    Assert.Equal(12_861, plane.Speed, 1);
    Assert.Equal(304.878, plane.VerticalSpeed, 3);
    Assert.Equal(0x7700, plane.Squawk);
  }

  [Fact]
  public void Mode_s_decoder_accepts_valid_cpr_pair_in_avr_and_beast_framing() {
    const string even = "*8D75804B580FF2CF7E9BA6F701D0;";
    const string odd = "*8D75804B580FF6B283EB7A157117;";
    var decoder = new ExternalAdsbDecoder();

    decoder.TryDecodeLine(even, out _);
    Assert.True(decoder.TryDecodeLine(odd, out var avrPlane));
    Assert.Equal("75804B", avrPlane.Tag);
    Assert.NotEqual(0, avrPlane.Lat);
    Assert.NotEqual(0, avrPlane.Lng);

    decoder.TryDecodeBeast(Beast(even), out _);
    Assert.True(decoder.TryDecodeBeast(Beast(odd), out var beastPlane));
    Assert.Equal("75804B", beastPlane.Tag);

    string corrupted = odd[..^2] + "0;";
    Assert.False(new ExternalAdsbDecoder().TryDecodeLine(corrupted, out _));
  }

  [Fact]
  public async Task Http_poll_uses_upstream_url_and_user_agent_and_decodes_units() {
    const string json = """
        {"ac":[
          {"hex":"abc123","lat":34.5,"lon":33.25,"alt_baro":10000,"track":91.5,
           "gs":250,"baro_rate":600,"flight":" test123 ","squawk":"7700"},
          {"hex":"def456","lat":35,"lon":34,"alt_baro":"ground","track":0,
           "gs":0,"baro_rate":0,"flight":"","squawk":""}
        ]}
        """;
    var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
      Content = new StringContent(json, Encoding.UTF8, "application/json"),
    });
    using var http = new HttpClient(handler);
    var planes = new List<adsb.PointLatLngAltHdg>();
    await using var receiver = new ExternalAdsbReceiver(planes.Add, () => (34.5, 33.25), http);

    var result = await receiver.PollHttpOnceAsync(
        new ExternalAdsbOptions(true, "https://api.adsb.lol/", 30003), 100,
        CancellationToken.None);

    Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    Assert.Equal(2, result.AircraftCount);
    Assert.Equal("https://api.adsb.lol/v2/point/34.5/33.25/100", handler.LastUri?.ToString());
    Assert.StartsWith("Mission-Planner/", handler.LastUserAgent);
    var plane = Assert.Single(planes, value => value.Tag == "ABC123");
    Assert.Equal(3048, plane.Alt, 3);
    Assert.Equal(12_861, plane.Speed, 1);
    Assert.Equal("TEST123", plane.CallSign);
    Assert.Equal(0x7700, plane.Squawk);
  }

  [Fact]
  public async Task Loopback_tcp_receiver_starts_stops_and_delivers_sbs_without_thread_abort() {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var delivered = new TaskCompletionSource<adsb.PointLatLngAltHdg>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var writeAfterStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int deliveries = 0;
    await using var receiver = new ExternalAdsbReceiver(
        plane => {
          Interlocked.Increment(ref deliveries);
          delivered.TrySetResult(plane);
        }, () => (34.5, 33.25));
    Task server = Task.Run(async () => {
      using TcpClient client = await listener.AcceptTcpClientAsync();
      await using NetworkStream stream = client.GetStream();
      string data = Sbs("4", "ABC123", fields => {
        fields[12] = "120";
        fields[13] = "45";
      }) + "\r\n" + Sbs("3", "ABC123", fields => {
        fields[11] = "1000";
        fields[14] = "34.5";
        fields[15] = "33.25";
        fields[16] = "0";
        fields[17] = "1200";
      }) + "\r\n";
      await stream.WriteAsync(Encoding.ASCII.GetBytes(data));
      await stream.FlushAsync();
      await writeAfterStop.Task;
      try {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(data));
        await stream.FlushAsync();
      } catch (IOException) {
        // Configure(false) closes the active socket before it returns.
      }
    });

    await receiver.ConfigureAsync(new ExternalAdsbOptions(true, "127.0.0.1", port));
    adsb.PointLatLngAltHdg plane = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await receiver.ConfigureAsync(new ExternalAdsbOptions(false, "", port));
    writeAfterStop.TrySetResult();
    listener.Stop();
    await server.WaitAsync(TimeSpan.FromSeconds(2));
    await Task.Delay(100);

    Assert.Equal("ABC123", plane.Tag);
    Assert.Equal(45, plane.Heading);
    Assert.Equal(1, Volatile.Read(ref deliveries));
    Assert.Equal("external ADS-B disabled", receiver.Status);
  }

  [Fact]
  public async Task Receiver_disposal_is_idempotent() {
    var receiver = new ExternalAdsbReceiver(_ => { }, () => null);

    await receiver.DisposeAsync();
    await receiver.DisposeAsync();
  }

  private static string Sbs(string transmissionType, string id, Action<string[]> fill) {
    var fields = new string[22];
    Array.Fill(fields, "");
    fields[0] = "MSG";
    fields[1] = transmissionType;
    fields[2] = "1";
    fields[3] = "1";
    fields[4] = id;
    fields[5] = "1";
    fields[6] = "2026/08/21";
    fields[7] = "12:00:00.000";
    fields[8] = fields[6];
    fields[9] = fields[7];
    fill(fields);
    return string.Join(',', fields);
  }

  private static byte[] Beast(string avr) {
    byte[] payload = adsb.ConvertHexStringToByteArray(avr.Trim('*', ';'));
    var frame = new byte[23];
    frame[0] = 0x1a;
    frame[1] = (byte)'3';
    Array.Copy(payload, 0, frame, 9, payload.Length);
    return frame;
  }

  private sealed class RecordingHandler(
      Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler {
    internal Uri? LastUri { get; private set; }
    internal string LastUserAgent { get; private set; } = "";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {
      LastUri = request.RequestUri;
      LastUserAgent = request.Headers.UserAgent.ToString();
      return Task.FromResult(response(request));
    }
  }
}
