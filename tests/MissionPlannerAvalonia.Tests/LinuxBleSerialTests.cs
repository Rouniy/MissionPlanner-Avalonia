using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class LinuxBleSerialTests {
  private const string Endpoint = "BLE_test_AABBCCDDEEFF";

  [Fact]
  public void Endpoint_round_trips_address_and_sanitizes_display_name() {
    string endpoint = BleEndpoint.Create("  Radio/one\n", "aa:bb:cc:dd:ee:ff");

    Assert.Equal("BLE_Radio-one_AABBCCDDEEFF", endpoint);
    Assert.True(BleEndpoint.TryAddress(endpoint, out string address));
    Assert.Equal("AA:BB:CC:DD:EE:FF", address);
    Assert.True(ConnectionViewModel.IsBleEndpoint(endpoint));
    Assert.False(BleEndpoint.TryAddress("BLE_missing-address", out _));
  }

  [Fact]
  public void Endpoint_rejects_invalid_hardware_address() {
    Assert.Throws<ArgumentException>(() => BleEndpoint.Create("radio", "AA:BB"));
    Assert.False(BleEndpoint.TryAddress("BLE_radio_AABBCCDDEEFZ", out _));
  }

  [Fact]
  public void Open_keeps_notification_delivered_before_backend_returns() {
    var backend = new FakeBackend { InitialNotification = [1, 2, 3] };
    using var serial = new LinuxBleSerial(Endpoint, backend);

    serial.Open();
    var destination = Enumerable.Repeat((byte)0xee, 7).ToArray();
    int read = serial.Read(destination, 2, 3);

    Assert.True(serial.IsOpen);
    Assert.Equal("AA:BB:CC:DD:EE:FF", backend.OpenedAddress);
    Assert.Equal(3, read);
    Assert.Equal([0xee, 0xee, 1, 2, 3, 0xee, 0xee], destination);
  }

  [Fact]
  public void Write_forwards_only_requested_buffer_slice() {
    var backend = new FakeBackend();
    using var serial = new LinuxBleSerial(Endpoint, backend);
    serial.Open();

    serial.Write([10, 11, 12, 13, 14], 1, 3);

    Assert.Equal([11, 12, 13], Assert.Single(backend.Session.Writes));
  }

  [Fact]
  public async Task Remote_disconnect_closes_stream_and_wakes_reader() {
    var backend = new FakeBackend();
    using var serial = new LinuxBleSerial(Endpoint, backend) { ReadTimeout = 5000 };
    serial.Open();
    var destination = new byte[1];
    Task<int> pendingRead = Task.Run(() => serial.Read(destination, 0, 1));
    await Task.Delay(50);

    backend.Disconnect();

    Assert.Equal(0, await pendingRead.WaitAsync(TimeSpan.FromSeconds(1)));
    Assert.False(serial.IsOpen);
    Assert.Throws<IOException>(() => serial.Write([1], 0, 1));
  }

  [Fact]
  public async Task Close_cancels_an_open_that_never_answers() {
    var backend = new FakeBackend { BlockOpen = true };
    using var serial = new LinuxBleSerial(Endpoint, backend);
    Task open = Task.Run(serial.Open);
    await backend.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

    serial.Close();

    IOException error = await Assert.ThrowsAsync<IOException>(
        async () => await open.WaitAsync(TimeSpan.FromSeconds(1)));
    Assert.IsType<TaskCanceledException>(error.InnerException);
    Assert.False(serial.IsOpen);
  }

  private sealed class FakeBackend : IBleUartBackend {
    private Action? _disconnected;

    internal byte[]? InitialNotification { get; init; }
    internal bool BlockOpen { get; init; }
    internal string? OpenedAddress { get; private set; }
    internal FakeSession Session { get; } = new();
    internal TaskCompletionSource OpenStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<BleDeviceInfo>> DiscoverAsync(
        TimeSpan duration, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BleDeviceInfo>>([]);

    public async Task<IBleUartSession> OpenAsync(
        string address,
        Action<ReadOnlyMemory<byte>> received,
        Action disconnected,
        CancellationToken cancellationToken) {
      OpenedAddress = address;
      _disconnected = disconnected;
      OpenStarted.TrySetResult();
      if (BlockOpen) {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      }
      if (InitialNotification != null) {
        received(InitialNotification);
      }
      return Session;
    }

    internal void Disconnect() {
      Session.Connected = false;
      _disconnected?.Invoke();
    }
  }

  private sealed class FakeSession : IBleUartSession {
    internal List<byte[]> Writes { get; } = [];
    internal bool Connected { get; set; } = true;
    public bool IsConnected => Connected;

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) {
      Writes.Add(data.ToArray());
      return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken) {
      Connected = false;
      return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
