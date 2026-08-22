using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class PortableWebSocketSerialTests {
  [Fact]
  public void Factory_uses_managed_transport_and_normalizes_legacy_http_urls() {
    using var stream = ConnectionViewModel.CreateConfiguredNetworkStream(
        "WS", "http://user:p%40ss@localhost:8080/telemetry", "");

    var webSocket = Assert.IsType<PortableWebSocketSerial>(stream);
    Assert.Equal("ws", webSocket.Endpoint.Scheme);
    Assert.Empty(webSocket.Endpoint.UserInfo);
    Assert.False(webSocket.UsesSocketIo);
  }

  [Theory]
  [InlineData("ws://localhost/socket.io/?EIO=4", true)]
  [InlineData("wss://localhost/telemetry?EIO=3", true)]
  [InlineData("ws://localhost/raw", false)]
  public void Socket_io_mode_is_enabled_only_for_explicit_socket_io_urls(
      string value, bool expected) {
    using var stream = new PortableWebSocketSerial(value);

    Assert.Equal(expected, stream.UsesSocketIo);
  }

  [Fact]
  public async Task Raw_websocket_round_trips_fragmented_binary_without_socket_io_probe() {
    await using var server = await LocalWebSocketServer.StartAsync();
    Task<WebSocket> accepting = server.AcceptAsync();
    using var stream = new PortableWebSocketSerial(server.WebSocketUri.ToString()) {
      AutoReconnect = false,
      ReadTimeout = 1000,
      WriteTimeout = 1000,
    };

    stream.Open();
    using WebSocket peer = await accepting.WaitAsync(TimeSpan.FromSeconds(3));
    var outgoing = new byte[32];
    Task<WebSocketReceiveResult> outgoingRead = peer.ReceiveAsync(
        new ArraySegment<byte>(outgoing), CancellationToken.None);
    await Task.Delay(150);
    Assert.False(outgoingRead.IsCompleted,
        "A raw WebSocket connection must not receive the upstream Socket.IO 2probe payload.");

    await peer.SendAsync(new ArraySegment<byte>(new byte[] { 1, 2 }),
        WebSocketMessageType.Binary, false, CancellationToken.None);
    await peer.SendAsync(new ArraySegment<byte>(new byte[] { 3, 4 }),
        WebSocketMessageType.Binary, true, CancellationToken.None);
    await WaitUntilAsync(() => stream.BytesToRead == 4, TimeSpan.FromSeconds(2));
    var incoming = new byte[4];
    Assert.Equal(4, stream.Read(incoming, 0, incoming.Length));
    Assert.Equal(new byte[] { 1, 2, 3, 4 }, incoming);

    stream.Write(new byte[] { 5, 6, 7 }, 0, 3);
    WebSocketReceiveResult sent = await outgoingRead.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal(WebSocketMessageType.Binary, sent.MessageType);
    Assert.Equal(3, sent.Count);
    Assert.Equal(new byte[] { 5, 6, 7 }, outgoing[..sent.Count]);

    var stopwatch = Stopwatch.StartNew();
    stream.Close();
    stopwatch.Stop();
    Assert.False(stream.IsOpen);
    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
  }

  [Fact]
  public async Task Remote_disconnect_reconnects_once_and_close_stops_the_lifetime() {
    await using var server = await LocalWebSocketServer.StartAsync();
    Task<WebSocket> firstAccept = server.AcceptAsync();
    using var stream = new PortableWebSocketSerial(server.WebSocketUri.ToString()) {
      AutoReconnect = true,
      ReconnectAttempts = 2,
      ReadTimeout = 1000,
    };

    stream.Open();
    using WebSocket first = await firstAccept.WaitAsync(TimeSpan.FromSeconds(3));
    Task<WebSocket> secondAccept = server.AcceptAsync();
    first.Abort();
    first.Dispose();

    await Task.Delay(100);
    Assert.True(stream.IsOpen,
        "The transport must remain logically open while its bounded reconnect is running.");
    using WebSocket second = await secondAccept.WaitAsync(TimeSpan.FromSeconds(5));
    await WaitUntilAsync(() => stream.IsOpen, TimeSpan.FromSeconds(2));
    await second.SendAsync(new ArraySegment<byte>(new byte[] { 9, 8 }),
        WebSocketMessageType.Binary, true, CancellationToken.None);
    await WaitUntilAsync(() => stream.BytesToRead == 2, TimeSpan.FromSeconds(2));
    var data = new byte[2];
    Assert.Equal(2, stream.Read(data, 0, data.Length));
    Assert.Equal(new byte[] { 9, 8 }, data);

    stream.Close();
    Assert.False(stream.IsOpen);
  }

  [Fact]
  public async Task Explicit_socket_io_endpoint_performs_handshake_and_prefixes_binary_data() {
    await using var server = await LocalWebSocketServer.StartAsync();
    var socketIoEndpoint = new UriBuilder(server.WebSocketUri) {
      Path = "/socket.io/",
      Query = "EIO=4&transport=websocket",
    }.Uri;
    Task<WebSocket> accepting = server.AcceptAsync();
    using var stream = new PortableWebSocketSerial(socketIoEndpoint.ToString()) {
      AutoReconnect = false,
      WriteTimeout = 1000,
    };

    stream.Open();
    using WebSocket peer = await accepting.WaitAsync(TimeSpan.FromSeconds(3));
    await SendTextAsync(peer, "0{\"sid\":\"test\"}");
    Assert.Equal("2probe", await ReceiveTextAsync(peer));
    await SendTextAsync(peer, "3probe");
    Assert.Equal("5", await ReceiveTextAsync(peer));
    Assert.Equal("40/MAVControl,", await ReceiveTextAsync(peer));

    stream.Write(new byte[] { 7, 8 }, 0, 2);
    var buffer = new byte[3];
    WebSocketReceiveResult sent = await peer.ReceiveAsync(
        new ArraySegment<byte>(buffer), CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal(WebSocketMessageType.Binary, sent.MessageType);
    Assert.Equal(new byte[] { 4, 7, 8 }, buffer);
  }

  private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout) {
    DateTime deadline = DateTime.UtcNow + timeout;
    while (!condition()) {
      if (DateTime.UtcNow >= deadline) {
        throw new TimeoutException("The expected WebSocket state was not reached.");
      }
      await Task.Delay(10);
    }
  }

  private static Task SendTextAsync(WebSocket socket, string value) {
    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
    return socket.SendAsync(new ArraySegment<byte>(bytes),
        WebSocketMessageType.Text, true, CancellationToken.None);
  }

  private static async Task<string> ReceiveTextAsync(WebSocket socket) {
    var buffer = new byte[256];
    WebSocketReceiveResult result = await socket.ReceiveAsync(
        new ArraySegment<byte>(buffer), CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal(WebSocketMessageType.Text, result.MessageType);
    Assert.True(result.EndOfMessage);
    return System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
  }

  private sealed class LocalWebSocketServer : IAsyncDisposable {
    private readonly HttpListener _listener;

    private LocalWebSocketServer(HttpListener listener, Uri webSocketUri) {
      _listener = listener;
      WebSocketUri = webSocketUri;
    }

    internal Uri WebSocketUri { get; }

    internal static Task<LocalWebSocketServer> StartAsync() {
      int port = ReservePort();
      var listener = new HttpListener();
      listener.Prefixes.Add($"http://127.0.0.1:{port}/");
      listener.Start();
      return Task.FromResult(new LocalWebSocketServer(
          listener, new Uri($"ws://127.0.0.1:{port}/telemetry")));
    }

    internal async Task<WebSocket> AcceptAsync() {
      HttpListenerContext context = await _listener.GetContextAsync();
      HttpListenerWebSocketContext webSocket = await context.AcceptWebSocketAsync(null);
      return webSocket.WebSocket;
    }

    public ValueTask DisposeAsync() {
      _listener.Close();
      return ValueTask.CompletedTask;
    }

    private static int ReservePort() {
      using var listener = new TcpListener(IPAddress.Loopback, 0);
      listener.Start();
      return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
  }
}
