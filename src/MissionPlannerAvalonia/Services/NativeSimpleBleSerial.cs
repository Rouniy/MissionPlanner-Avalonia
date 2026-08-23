using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlannerAvalonia.Services;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct SimpleBleUuid {
  internal const int Capacity = 37;

  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = Capacity)]
  internal string Value;

  internal SimpleBleUuid(string value) {
    if (!Guid.TryParseExact(value, "D", out Guid parsed)) {
      throw new ArgumentException("A SimpleBLE UUID must use the canonical 36-character form.",
          nameof(value));
    }
    Value = parsed.ToString("D");
  }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SimpleBleNotificationCallback(
    SimpleBleUuid service,
    SimpleBleUuid characteristic,
    IntPtr data,
    nuint dataLength,
    IntPtr userData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SimpleBleDisconnectedCallback(IntPtr peripheral, IntPtr userData);

internal interface ISimpleBleNative {
  bool IsBluetoothEnabled();
  nuint AdapterCount();
  IntPtr GetAdapter(nuint index);
  void ReleaseAdapter(IntPtr adapter);
  void StartScan(IntPtr adapter);
  void StopScan(IntPtr adapter);
  nuint ScanResultCount(IntPtr adapter);
  IntPtr GetScanResult(IntPtr adapter, nuint index);
  string PeripheralIdentifier(IntPtr peripheral);
  string PeripheralAddress(IntPtr peripheral);
  IReadOnlyList<string> PeripheralServices(IntPtr peripheral);
  void Connect(IntPtr peripheral);
  void Disconnect(IntPtr peripheral);
  bool IsConnected(IntPtr peripheral);
  void Subscribe(
      IntPtr peripheral,
      SimpleBleUuid service,
      SimpleBleUuid characteristic,
      IntPtr callback,
      IntPtr userData);
  void Unsubscribe(
      IntPtr peripheral, SimpleBleUuid service, SimpleBleUuid characteristic);
  void SetDisconnectedCallback(IntPtr peripheral, IntPtr callback, IntPtr userData);
  void WriteRequest(
      IntPtr peripheral,
      SimpleBleUuid service,
      SimpleBleUuid characteristic,
      byte[] data);
  void ReleasePeripheral(IntPtr peripheral);
}

/// <summary>
/// SimpleBLE 0.7.3 C ABI used by official Mission Planner. Windows uses the pinned upstream
/// binaries; macOS uses the matching, checksum-pinned upstream CoreBluetooth dylibs.
/// </summary>
internal sealed class SimpleBleNative : ISimpleBleNative {
  private const string Library = "simpleble-c";
  private const int MaximumAdapters = 16;
  private const int MaximumServices = 256;
  private const int ServiceBufferBytes = 16 * 1024;

  internal static SimpleBleNative Instance { get; } = new();

  private SimpleBleNative() { }

  internal static (bool Enabled, nuint AdapterCount) Probe() =>
      (Instance.IsBluetoothEnabled(), Instance.AdapterCount());

  public bool IsBluetoothEnabled() => NativeMethods.simpleble_adapter_is_bluetooth_enabled();

  public nuint AdapterCount() {
    nuint count = NativeMethods.simpleble_adapter_get_count();
    if (count > MaximumAdapters) {
      throw new IOException($"SimpleBLE reported an invalid adapter count ({count}).");
    }
    return count;
  }

  public IntPtr GetAdapter(nuint index) => NativeMethods.simpleble_adapter_get_handle(index);

  public void ReleaseAdapter(IntPtr adapter) {
    if (adapter != IntPtr.Zero) {
      NativeMethods.simpleble_adapter_release_handle(adapter);
    }
  }

  public void StartScan(IntPtr adapter) => Check(
      NativeMethods.simpleble_adapter_scan_start(adapter), "start Bluetooth LE scan");

  public void StopScan(IntPtr adapter) => Check(
      NativeMethods.simpleble_adapter_scan_stop(adapter), "stop Bluetooth LE scan");

  public nuint ScanResultCount(IntPtr adapter) =>
      NativeMethods.simpleble_adapter_scan_get_results_count(adapter);

  public IntPtr GetScanResult(IntPtr adapter, nuint index) =>
      NativeMethods.simpleble_adapter_scan_get_results_handle(adapter, index);

  public string PeripheralIdentifier(IntPtr peripheral) => ReadOwnedString(
      NativeMethods.simpleble_peripheral_identifier(peripheral));

  public string PeripheralAddress(IntPtr peripheral) => ReadOwnedString(
      NativeMethods.simpleble_peripheral_address(peripheral));

  public IReadOnlyList<string> PeripheralServices(IntPtr peripheral) {
    nuint count = NativeMethods.simpleble_peripheral_services_count(peripheral);
    if (count > MaximumServices) {
      throw new IOException($"SimpleBLE reported an invalid service count ({count}).");
    }

    var services = new List<string>((int)count);
    IntPtr buffer = Marshal.AllocHGlobal(ServiceBufferBytes);
    try {
      var bytes = new byte[SimpleBleUuid.Capacity];
      for (nuint index = 0; index < count; index++) {
        Check(NativeMethods.simpleble_peripheral_services_get(peripheral, index, buffer),
            "read Bluetooth LE services");
        Marshal.Copy(buffer, bytes, 0, bytes.Length);
        int terminator = Array.IndexOf(bytes, (byte)0);
        int length = terminator < 0 ? bytes.Length : terminator;
        services.Add(Encoding.ASCII.GetString(bytes, 0, length));
      }
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
    return services;
  }

  public void Connect(IntPtr peripheral) => Check(
      NativeMethods.simpleble_peripheral_connect(peripheral), "connect Bluetooth LE peripheral");

  public void Disconnect(IntPtr peripheral) => Check(
      NativeMethods.simpleble_peripheral_disconnect(peripheral),
      "disconnect Bluetooth LE peripheral");

  public bool IsConnected(IntPtr peripheral) {
    Check(NativeMethods.simpleble_peripheral_is_connected(peripheral, out bool connected),
        "read Bluetooth LE connection state");
    return connected;
  }

  public void Subscribe(
      IntPtr peripheral,
      SimpleBleUuid service,
      SimpleBleUuid characteristic,
      IntPtr callback,
      IntPtr userData) => Check(NativeMethods.simpleble_peripheral_notify(
          peripheral, service, characteristic, callback, userData),
          "subscribe to Bluetooth LE notifications");

  public void Unsubscribe(
      IntPtr peripheral, SimpleBleUuid service, SimpleBleUuid characteristic) => Check(
      NativeMethods.simpleble_peripheral_unsubscribe(peripheral, service, characteristic),
      "unsubscribe from Bluetooth LE notifications");

  public void SetDisconnectedCallback(
      IntPtr peripheral, IntPtr callback, IntPtr userData) => Check(
      NativeMethods.simpleble_peripheral_set_callback_on_disconnected(
          peripheral, callback, userData), "set Bluetooth LE disconnect callback");

  public void WriteRequest(
      IntPtr peripheral,
      SimpleBleUuid service,
      SimpleBleUuid characteristic,
      byte[] data) => Check(NativeMethods.simpleble_peripheral_write_request(
          peripheral, service, characteristic, data, (nuint)data.Length),
          "write Bluetooth LE characteristic");

  public void ReleasePeripheral(IntPtr peripheral) {
    if (peripheral != IntPtr.Zero) {
      NativeMethods.simpleble_peripheral_release_handle(peripheral);
    }
  }

  private static string ReadOwnedString(IntPtr value) {
    if (value == IntPtr.Zero) {
      return "";
    }
    try {
      return Marshal.PtrToStringUTF8(value) ?? "";
    } finally {
      NativeMethods.simpleble_free(value);
    }
  }

  private static void Check(SimpleBleError error, string operation) {
    if (error != SimpleBleError.Success) {
      throw new IOException($"SimpleBLE could not {operation}.");
    }
  }

  private enum SimpleBleError {
    Success = 0,
    Failure = 1,
  }

  private static class NativeMethods {
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool simpleble_adapter_is_bluetooth_enabled();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nuint simpleble_adapter_get_count();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr simpleble_adapter_get_handle(nuint index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void simpleble_adapter_release_handle(IntPtr adapter);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern SimpleBleError simpleble_adapter_scan_start(IntPtr adapter);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern SimpleBleError simpleble_adapter_scan_stop(IntPtr adapter);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nuint simpleble_adapter_scan_get_results_count(IntPtr adapter);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr simpleble_adapter_scan_get_results_handle(
        IntPtr adapter, nuint index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void simpleble_peripheral_release_handle(IntPtr peripheral);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr simpleble_peripheral_identifier(IntPtr peripheral);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr simpleble_peripheral_address(IntPtr peripheral);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern SimpleBleError simpleble_peripheral_connect(IntPtr peripheral);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern SimpleBleError simpleble_peripheral_disconnect(IntPtr peripheral);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern SimpleBleError simpleble_peripheral_is_connected(
        IntPtr peripheral, [MarshalAs(UnmanagedType.I1)] out bool connected);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nuint simpleble_peripheral_services_count(IntPtr peripheral);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern SimpleBleError simpleble_peripheral_services_get(
        IntPtr peripheral, nuint index, IntPtr service);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern SimpleBleError simpleble_peripheral_notify(
        IntPtr peripheral,
        SimpleBleUuid service,
        SimpleBleUuid characteristic,
        IntPtr callback,
        IntPtr userData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern SimpleBleError simpleble_peripheral_unsubscribe(
        IntPtr peripheral, SimpleBleUuid service, SimpleBleUuid characteristic);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern SimpleBleError simpleble_peripheral_set_callback_on_disconnected(
        IntPtr peripheral, IntPtr callback, IntPtr userData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern SimpleBleError simpleble_peripheral_write_request(
        IntPtr peripheral,
        SimpleBleUuid service,
        SimpleBleUuid characteristic,
        [In] byte[] data,
        nuint dataLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void simpleble_free(IntPtr value);
  }
}

internal sealed class NativeSimpleBleBackend : IBleUartBackend {
  private const int MaximumScanResults = 4096;
  private static readonly TimeSpan MaximumScanDuration = TimeSpan.FromSeconds(30);

  internal static NativeSimpleBleBackend Instance { get; } =
      new(SimpleBleNative.Instance);

  private readonly ISimpleBleNative _native;
  private readonly TimeSpan _openScanDuration;
  private readonly SemaphoreSlim _scanGate = new(1, 1);

  internal NativeSimpleBleBackend(
      ISimpleBleNative native, TimeSpan? openScanDuration = null) {
    _native = native;
    _openScanDuration = openScanDuration ?? TimeSpan.FromSeconds(5);
  }

  public async Task<IReadOnlyList<BleDeviceInfo>> DiscoverAsync(
      TimeSpan duration, CancellationToken cancellationToken) {
    EnsurePlatform();
    duration = ClampDuration(duration);
    await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      return await Task.Run(() => DiscoverCore(duration, cancellationToken),
          CancellationToken.None).ConfigureAwait(false);
    } catch (Exception ex) when (IsNativeLoadFailure(ex)) {
      throw new IOException(
          "The SimpleBLE 0.7.3 native runtime could not be loaded for this platform.", ex);
    } finally {
      _scanGate.Release();
    }
  }

  public async Task<IBleUartSession> OpenAsync(
      string address,
      Action<ReadOnlyMemory<byte>> received,
      Action disconnected,
      CancellationToken cancellationToken) {
    EnsurePlatform();
    ArgumentException.ThrowIfNullOrWhiteSpace(address);

    IntPtr peripheral;
    await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      peripheral = await Task.Run(
          () => FindPeripheralCore(address, _openScanDuration, cancellationToken),
          CancellationToken.None).ConfigureAwait(false);
    } catch (Exception ex) when (IsNativeLoadFailure(ex)) {
      throw new IOException(
          "The SimpleBLE 0.7.3 native runtime could not be loaded for this platform.", ex);
    } finally {
      _scanGate.Release();
    }

    if (peripheral == IntPtr.Zero) {
      throw new IOException(
          $"BLE device {address} was not found. Ensure it is powered and in range.");
    }

    Task<IBleUartSession> worker = Task.Run(
        () => ConnectCore(peripheral, received, disconnected, cancellationToken),
        CancellationToken.None);
    try {
      return await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      _ = CloseAbandonedSessionAsync(worker);
      throw;
    } catch (Exception ex) when (IsNativeLoadFailure(ex)) {
      throw new IOException(
          "The SimpleBLE 0.7.3 native runtime could not be loaded for this platform.", ex);
    }
  }

  private IReadOnlyList<BleDeviceInfo> DiscoverCore(
      TimeSpan duration, CancellationToken cancellationToken) {
    var devices = new Dictionary<string, BleDeviceInfo>(StringComparer.OrdinalIgnoreCase);
    ScanCore(duration, cancellationToken, peripheral => {
      try {
        if (!_native.PeripheralServices(peripheral).Any(IsNordicUartService)) {
          return false;
        }
        string address = _native.PeripheralAddress(peripheral);
        string name = _native.PeripheralIdentifier(peripheral);
        string endpoint = BleEndpoint.Create(name, address);
        if (!BleEndpoint.TryAddress(endpoint, out string canonicalAddress)) {
          return false;
        }
        devices[canonicalAddress] = new BleDeviceInfo(
            string.IsNullOrWhiteSpace(name) ? "BLE device" : name,
            canonicalAddress, endpoint);
      } catch {
        // A malformed/unreadable advertisement must not hide other usable radios.
      }
      return false;
    });
    return devices.Values.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(device => device.Address, StringComparer.OrdinalIgnoreCase).ToArray();
  }

  private IntPtr FindPeripheralCore(
      string address, TimeSpan duration, CancellationToken cancellationToken) {
    IntPtr found = IntPtr.Zero;
    ScanCore(duration, cancellationToken, peripheral => {
      try {
        if (!AddressesEqual(_native.PeripheralAddress(peripheral), address)) {
          return false;
        }
        found = peripheral;
        return true;
      } catch {
        return false;
      }
    });
    return found;
  }

  private void ScanCore(
      TimeSpan duration,
      CancellationToken cancellationToken,
      Func<IntPtr, bool> retainPeripheral) {
    cancellationToken.ThrowIfCancellationRequested();
    if (!_native.IsBluetoothEnabled()) {
      throw new IOException(
          "Bluetooth is disabled or Mission Planner does not have Bluetooth permission.");
    }

    var adapters = new List<IntPtr>();
    var started = new List<IntPtr>();
    try {
      nuint adapterCount = _native.AdapterCount();
      for (nuint index = 0; index < adapterCount; index++) {
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr adapter = _native.GetAdapter(index);
        if (adapter != IntPtr.Zero) {
          adapters.Add(adapter);
        }
      }
      try {
        foreach (IntPtr adapter in adapters) {
          cancellationToken.ThrowIfCancellationRequested();
          _native.StartScan(adapter);
          started.Add(adapter);
        }

        if (duration > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(duration)) {
          cancellationToken.ThrowIfCancellationRequested();
        }
      } finally {
        foreach (IntPtr adapter in started.AsEnumerable().Reverse()) {
          try {
            _native.StopScan(adapter);
          } catch {
          }
        }
      }

      cancellationToken.ThrowIfCancellationRequested();
      foreach (IntPtr adapter in adapters) {
        nuint resultCount = _native.ScanResultCount(adapter);
        if (resultCount > MaximumScanResults) {
          throw new IOException($"SimpleBLE reported an invalid scan result count ({resultCount}).");
        }
        for (nuint index = 0; index < resultCount; index++) {
          cancellationToken.ThrowIfCancellationRequested();
          IntPtr peripheral = _native.GetScanResult(adapter, index);
          if (peripheral == IntPtr.Zero) {
            continue;
          }
          bool retained = false;
          try {
            retained = retainPeripheral(peripheral);
            if (retained) {
              return;
            }
          } finally {
            if (!retained) {
              _native.ReleasePeripheral(peripheral);
            }
          }
        }
      }
    } finally {
      foreach (IntPtr adapter in adapters) {
        _native.ReleaseAdapter(adapter);
      }
      adapters.Clear();
    }
  }

  private IBleUartSession ConnectCore(
      IntPtr peripheral,
      Action<ReadOnlyMemory<byte>> received,
      Action disconnected,
      CancellationToken cancellationToken) {
    NativeSimpleBleSession? session = null;
    using CancellationTokenRegistration registration = cancellationToken.Register(() => {
      try {
        _native.Disconnect(peripheral);
      } catch {
      }
    });

    try {
      cancellationToken.ThrowIfCancellationRequested();
      _native.Connect(peripheral);
      cancellationToken.ThrowIfCancellationRequested();
      if (!_native.IsConnected(peripheral)) {
        throw new IOException("The Bluetooth LE peripheral disconnected while opening.");
      }

      DateTime serviceDeadline = DateTime.UtcNow.AddSeconds(10);
      while (!_native.PeripheralServices(peripheral).Any(IsNordicUartService)) {
        if (DateTime.UtcNow >= serviceDeadline) {
          throw new IOException(
              $"BLE device does not expose Nordic UART service {LinuxBluezBleBackend.ServiceUuid}.");
        }
        if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100))) {
          cancellationToken.ThrowIfCancellationRequested();
        }
      }

      session = new NativeSimpleBleSession(_native, peripheral, received, disconnected);
      session.Initialize();
      cancellationToken.ThrowIfCancellationRequested();
      if (!_native.IsConnected(peripheral)) {
        throw new IOException("The Bluetooth LE peripheral disconnected while opening.");
      }
      return session;
    } catch {
      if (session != null) {
        try {
          session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        } catch {
        }
      } else {
        try {
          _native.Disconnect(peripheral);
        } catch {
        }
        _native.ReleasePeripheral(peripheral);
      }
      throw;
    }
  }

  private void EnsurePlatform() {
    if (_native is SimpleBleNative && !OperatingSystem.IsMacOS() &&
        !OperatingSystem.IsWindows()) {
      throw new PlatformNotSupportedException(
          "The native SimpleBLE transport is available only on macOS and Windows.");
    }
  }

  private static bool AddressesEqual(string left, string right) {
    string Compact(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
    return string.Equals(Compact(left), Compact(right), StringComparison.Ordinal);
  }

  private static bool IsNordicUartService(string uuid) => string.Equals(
      uuid, LinuxBluezBleBackend.ServiceUuid, StringComparison.OrdinalIgnoreCase);

  private static TimeSpan ClampDuration(TimeSpan duration) {
    if (duration <= TimeSpan.Zero) {
      return TimeSpan.Zero;
    }
    return duration > MaximumScanDuration ? MaximumScanDuration : duration;
  }

  private static bool IsNativeLoadFailure(Exception exception) =>
      exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException;

  private static async Task CloseAbandonedSessionAsync(Task<IBleUartSession> worker) {
    try {
      IBleUartSession session = await worker.ConfigureAwait(false);
      using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
      try {
        await session.CloseAsync(cancellation.Token).ConfigureAwait(false);
      } catch {
      }
      await session.DisposeAsync().ConfigureAwait(false);
    } catch {
    }
  }
}

internal sealed class NativeSimpleBleSession : IBleUartSession {
  private const int SafeGattPayload = 20;
  private const int MaximumNotificationBytes = 64 * 1024;

  private static readonly ConcurrentDictionary<long, NativeSimpleBleSession> Sessions = new();
  private static readonly SimpleBleNotificationCallback NotificationCallback = OnNotification;
  private static readonly SimpleBleDisconnectedCallback DisconnectedCallback = OnDisconnected;
  private static readonly IntPtr NotificationCallbackPointer =
      Marshal.GetFunctionPointerForDelegate(NotificationCallback);
  private static readonly IntPtr DisconnectedCallbackPointer =
      Marshal.GetFunctionPointerForDelegate(DisconnectedCallback);
  private static long _nextSessionId;

  private readonly object _closeSync = new();
  private readonly SemaphoreSlim _operationGate = new(1, 1);
  private readonly ISimpleBleNative _native;
  private readonly Action<ReadOnlyMemory<byte>> _received;
  private readonly BleSessionConnection _connection;
  private readonly long _sessionId;
  private readonly IntPtr _userData;
  private readonly SimpleBleUuid _service = new(LinuxBluezBleBackend.ServiceUuid);
  private readonly SimpleBleUuid _write = new(LinuxBluezBleBackend.WriteUuid);
  private readonly SimpleBleUuid _notify = new(LinuxBluezBleBackend.NotifyUuid);

  private IntPtr _peripheral;
  private Task? _closeTask;
  private int _initialized;
  private int _closed;

  internal NativeSimpleBleSession(
      ISimpleBleNative native,
      IntPtr peripheral,
      Action<ReadOnlyMemory<byte>> received,
      Action disconnected) {
    _native = native;
    _peripheral = peripheral;
    _received = received;
    _connection = new BleSessionConnection(disconnected);
    _sessionId = Interlocked.Increment(ref _nextSessionId);
    _userData = new IntPtr(_sessionId);
  }

  public bool IsConnected => Volatile.Read(ref _closed) == 0 && _connection.IsConnected;

  internal void Initialize() {
    if (Interlocked.Exchange(ref _initialized, 1) != 0) {
      return;
    }
    if (!Sessions.TryAdd(_sessionId, this)) {
      throw new IOException("Could not allocate a SimpleBLE callback identity.");
    }
    try {
      _native.SetDisconnectedCallback(
          _peripheral, DisconnectedCallbackPointer, _userData);
      _native.Subscribe(
          _peripheral, _service, _notify, NotificationCallbackPointer, _userData);
    } catch {
      Sessions.TryRemove(_sessionId, out _);
      throw;
    }
  }

  public async Task WriteAsync(
      ReadOnlyMemory<byte> data, CancellationToken cancellationToken) {
    if (data.IsEmpty) {
      return;
    }
    if (!IsConnected) {
      throw new IOException("The BLE device is disconnected.");
    }

    await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    if (!IsConnected || _peripheral == IntPtr.Zero) {
      _operationGate.Release();
      throw new IOException("The BLE device is disconnected.");
    }
    byte[] exact = data.ToArray();
    IntPtr peripheral = _peripheral;
    Task worker = Task.Run(() => {
      using CancellationTokenRegistration registration = cancellationToken.Register(() => {
        try {
          _native.Disconnect(peripheral);
        } catch {
        }
      });
      for (int offset = 0; offset < exact.Length; offset += SafeGattPayload) {
        cancellationToken.ThrowIfCancellationRequested();
        int count = Math.Min(SafeGattPayload, exact.Length - offset);
        byte[] chunk = exact.AsSpan(offset, count).ToArray();
        _native.WriteRequest(peripheral, _service, _write, chunk);
      }
    }, CancellationToken.None);

    try {
      await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    } finally {
      if (worker.IsCompleted) {
        _operationGate.Release();
      } else {
        _ = worker.ContinueWith(_ => _operationGate.Release(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
      }
    }
  }

  public Task CloseAsync(CancellationToken cancellationToken) {
    Task closeTask;
    lock (_closeSync) {
      _closeTask ??= Task.Run(CloseCore, CancellationToken.None);
      closeTask = _closeTask;
    }
    return cancellationToken.CanBeCanceled
        ? closeTask.WaitAsync(cancellationToken)
        : closeTask;
  }

  public async ValueTask DisposeAsync() {
    try {
      using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      await CloseAsync(cancellation.Token).ConfigureAwait(false);
    } catch {
    }
  }

  private void CloseCore() {
    if (Interlocked.Exchange(ref _closed, 1) != 0) {
      return;
    }
    _connection.Disconnect();
    Sessions.TryRemove(_sessionId, out _);

    _operationGate.Wait();
    try {
      IntPtr peripheral = Interlocked.Exchange(ref _peripheral, IntPtr.Zero);
      if (peripheral == IntPtr.Zero) {
        return;
      }
      try {
        _native.Unsubscribe(peripheral, _service, _notify);
      } catch {
      }
      try {
        _native.SetDisconnectedCallback(peripheral, IntPtr.Zero, IntPtr.Zero);
      } catch {
      }
      try {
        if (_native.IsConnected(peripheral)) {
          _native.Disconnect(peripheral);
        }
      } catch {
      }
      _native.ReleasePeripheral(peripheral);
    } finally {
      _operationGate.Release();
    }
  }

  private void Receive(IntPtr data, nuint dataLength) {
    if (!IsConnected || data == IntPtr.Zero || dataLength == 0) {
      return;
    }
    if (dataLength > MaximumNotificationBytes) {
      _connection.Disconnect();
      return;
    }
    try {
      var bytes = new byte[(int)dataLength];
      Marshal.Copy(data, bytes, 0, bytes.Length);
      _received(bytes);
    } catch {
      _connection.Disconnect();
    }
  }

  private static void OnNotification(
      SimpleBleUuid service,
      SimpleBleUuid characteristic,
      IntPtr data,
      nuint dataLength,
      IntPtr userData) {
    try {
      if (Sessions.TryGetValue(userData.ToInt64(), out NativeSimpleBleSession? session)) {
        session.Receive(data, dataLength);
      }
    } catch {
      // Never allow a managed exception to unwind through CoreBluetooth/SimpleBLE.
    }
  }

  private static void OnDisconnected(IntPtr peripheral, IntPtr userData) {
    try {
      if (Sessions.TryGetValue(userData.ToInt64(), out NativeSimpleBleSession? session)) {
        session._connection.Disconnect();
      }
    } catch {
      // Never allow a managed exception to unwind through CoreBluetooth/SimpleBLE.
    }
  }
}
