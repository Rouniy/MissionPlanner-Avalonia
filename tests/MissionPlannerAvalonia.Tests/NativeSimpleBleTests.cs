using System.Runtime.InteropServices;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class NativeSimpleBleTests {
  private const string Address = "A4DC7196-F8EA-4E0E-B766-6608AADB1FC4";

  [Fact]
  public void Native_uuid_matches_the_simpleble_c_abi() {
    var uuid = new SimpleBleUuid(LinuxBluezBleBackend.ServiceUuid);

    Assert.Equal(37, Marshal.SizeOf<SimpleBleUuid>());
    Assert.Equal(LinuxBluezBleBackend.ServiceUuid, uuid.Value);
  }

  [Fact]
  public async Task Discovery_filters_nordic_uart_and_releases_every_native_handle() {
    var native = new FakeSimpleBleNative();
    native.AddPeripheral(new IntPtr(101), "Telemetry", Address,
        LinuxBluezBleBackend.ServiceUuid);
    native.AddPeripheral(new IntPtr(102), "Heart rate", "11:22:33:44:55:66",
        "0000180d-0000-1000-8000-00805f9b34fb");
    var backend = new NativeSimpleBleBackend(native, TimeSpan.Zero);

    IReadOnlyList<BleDeviceInfo> devices = await backend.DiscoverAsync(
        TimeSpan.Zero, CancellationToken.None);

    BleDeviceInfo device = Assert.Single(devices);
    Assert.Equal("Telemetry", device.Name);
    Assert.Equal(Address, device.Address);
    Assert.Equal("BLE_Telemetry_A4DC7196F8EA4E0EB7666608AADB1FC4", device.Endpoint);
    Assert.Equal(1, native.ScanStarts);
    Assert.Equal(1, native.ScanStops);
    Assert.Equal([new IntPtr(101), new IntPtr(102)], native.ReleasedPeripherals);
    Assert.Equal([native.Adapter], native.ReleasedAdapters);
  }

  [Fact]
  public async Task Cancelling_scan_stops_adapter_and_releases_handles() {
    var native = new FakeSimpleBleNative();
    var backend = new NativeSimpleBleBackend(native, TimeSpan.Zero);
    using var cancellation = new CancellationTokenSource();
    Task<IReadOnlyList<BleDeviceInfo>> scan = backend.DiscoverAsync(
        TimeSpan.FromSeconds(10), cancellation.Token);
    await native.ScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scan);
    Assert.Equal(1, native.ScanStops);
    Assert.Equal([native.Adapter], native.ReleasedAdapters);
  }

  [Fact]
  public async Task Session_delivers_notifications_chunks_writes_and_handles_remote_disconnect() {
    var native = new FakeSimpleBleNative();
    native.AddPeripheral(new IntPtr(101), "Telemetry", Address,
        LinuxBluezBleBackend.ServiceUuid);
    var backend = new NativeSimpleBleBackend(native, TimeSpan.Zero);
    var received = new List<byte[]>();
    int disconnects = 0;

    IBleUartSession session = await backend.OpenAsync(Address,
        data => received.Add(data.ToArray()), () => disconnects++, CancellationToken.None);
    native.Notify([1, 2, 3]);
    await session.WriteAsync(Enumerable.Range(0, 45).Select(value => (byte)value).ToArray(),
        CancellationToken.None);

    Assert.True(session.IsConnected);
    Assert.Equal([1, 2, 3], Assert.Single(received));
    Assert.Equal([20, 20, 5], native.Writes.Select(write => write.Length));
    native.RemoteDisconnect();
    Assert.False(session.IsConnected);
    Assert.Equal(1, disconnects);

    await session.CloseAsync(CancellationToken.None);
    await session.DisposeAsync();
    Assert.Equal(1, native.Unsubscribes);
    Assert.Equal(IntPtr.Zero, native.DisconnectCallbackPointer);
    Assert.Contains(new IntPtr(101), native.ReleasedPeripherals);
  }

  [Fact]
  public async Task Cancellation_interrupts_connect_and_worker_releases_peripheral() {
    var native = new FakeSimpleBleNative { BlockConnect = true };
    native.AddPeripheral(new IntPtr(101), "Telemetry", Address,
        LinuxBluezBleBackend.ServiceUuid);
    var backend = new NativeSimpleBleBackend(native, TimeSpan.Zero);
    using var cancellation = new CancellationTokenSource();
    Task<IBleUartSession> opening = backend.OpenAsync(
        Address, _ => { }, () => { }, cancellation.Token);
    await native.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await opening);
    Assert.True(SpinWait.SpinUntil(
        () => native.ReleasedPeripherals.Contains(new IntPtr(101)), TimeSpan.FromSeconds(1)));
    Assert.False(native.Connected);
  }

  [Fact]
  public void Mac_native_backend_loads_and_enumerates_on_the_native_runner() {
    if (!OperatingSystem.IsMacOS()) {
      return;
    }

    (_, nuint adapterCount) = SimpleBleNative.Probe();
    Assert.InRange(adapterCount, 0U, 16U);
  }

  private sealed class FakeSimpleBleNative : ISimpleBleNative {
    private readonly Dictionary<IntPtr, Peripheral> _peripherals = new();
    private readonly ManualResetEventSlim _connectRelease = new(false);

    internal IntPtr Adapter { get; } = new(1);
    internal int ScanStarts { get; private set; }
    internal int ScanStops { get; private set; }
    internal int Unsubscribes { get; private set; }
    internal bool Connected { get; private set; }
    internal bool BlockConnect { get; init; }
    internal IntPtr NotificationCallbackPointer { get; private set; }
    internal IntPtr NotificationUserData { get; private set; }
    internal IntPtr DisconnectCallbackPointer { get; private set; }
    internal IntPtr DisconnectUserData { get; private set; }
    internal List<IntPtr> ReleasedAdapters { get; } = new();
    internal List<IntPtr> ReleasedPeripherals { get; } = new();
    internal List<byte[]> Writes { get; } = new();
    internal TaskCompletionSource ScanStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ConnectStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsBluetoothEnabled() => true;
    public nuint AdapterCount() => 1;
    public IntPtr GetAdapter(nuint index) => Adapter;
    public void ReleaseAdapter(IntPtr adapter) => ReleasedAdapters.Add(adapter);

    public void StartScan(IntPtr adapter) {
      ScanStarts++;
      ScanStarted.TrySetResult();
    }

    public void StopScan(IntPtr adapter) => ScanStops++;
    public nuint ScanResultCount(IntPtr adapter) => (nuint)_peripherals.Count;
    public IntPtr GetScanResult(IntPtr adapter, nuint index) =>
        _peripherals.Keys.Order().ElementAt((int)index);
    public string PeripheralIdentifier(IntPtr peripheral) => _peripherals[peripheral].Name;
    public string PeripheralAddress(IntPtr peripheral) => _peripherals[peripheral].Address;
    public IReadOnlyList<string> PeripheralServices(IntPtr peripheral) =>
        _peripherals[peripheral].Services;

    public void Connect(IntPtr peripheral) {
      ConnectStarted.TrySetResult();
      if (BlockConnect) {
        _connectRelease.Wait(TimeSpan.FromSeconds(2));
      } else {
        Connected = true;
      }
    }

    public void Disconnect(IntPtr peripheral) {
      Connected = false;
      _connectRelease.Set();
    }

    public bool IsConnected(IntPtr peripheral) => Connected;

    public void Subscribe(
        IntPtr peripheral,
        SimpleBleUuid service,
        SimpleBleUuid characteristic,
        IntPtr callback,
        IntPtr userData) {
      NotificationCallbackPointer = callback;
      NotificationUserData = userData;
    }

    public void Unsubscribe(
        IntPtr peripheral, SimpleBleUuid service, SimpleBleUuid characteristic) {
      Unsubscribes++;
      NotificationCallbackPointer = IntPtr.Zero;
      NotificationUserData = IntPtr.Zero;
    }

    public void SetDisconnectedCallback(
        IntPtr peripheral, IntPtr callback, IntPtr userData) {
      DisconnectCallbackPointer = callback;
      DisconnectUserData = userData;
    }

    public void WriteRequest(
        IntPtr peripheral,
        SimpleBleUuid service,
        SimpleBleUuid characteristic,
        byte[] data) => Writes.Add(data.ToArray());

    public void ReleasePeripheral(IntPtr peripheral) => ReleasedPeripherals.Add(peripheral);

    internal void AddPeripheral(
        IntPtr handle, string name, string address, params string[] services) =>
        _peripherals.Add(handle, new Peripheral(name, address, services));

    internal void Notify(byte[] data) {
      SimpleBleNotificationCallback callback =
          Marshal.GetDelegateForFunctionPointer<SimpleBleNotificationCallback>(
              NotificationCallbackPointer);
      IntPtr nativeData = Marshal.AllocHGlobal(data.Length);
      try {
        Marshal.Copy(data, 0, nativeData, data.Length);
        callback(new SimpleBleUuid(LinuxBluezBleBackend.ServiceUuid),
            new SimpleBleUuid(LinuxBluezBleBackend.NotifyUuid), nativeData,
            (nuint)data.Length, NotificationUserData);
      } finally {
        Marshal.FreeHGlobal(nativeData);
      }
    }

    internal void RemoteDisconnect() {
      Connected = false;
      SimpleBleDisconnectedCallback callback =
          Marshal.GetDelegateForFunctionPointer<SimpleBleDisconnectedCallback>(
              DisconnectCallbackPointer);
      callback(new IntPtr(101), DisconnectUserData);
    }

    private sealed record Peripheral(
        string Name, string Address, IReadOnlyList<string> Services);
  }
}
