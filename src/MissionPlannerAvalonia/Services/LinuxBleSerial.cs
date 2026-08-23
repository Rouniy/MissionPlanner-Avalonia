using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using MissionPlanner.Comms;

namespace MissionPlannerAvalonia.Services;

internal sealed record BleDeviceInfo(string Name, string Address, string Endpoint);

internal static class BleEndpoint {
  internal const string Prefix = "BLE_";

  internal static string Create(string? name, string address) {
    string compactAddress = new(address.Where(Uri.IsHexDigit).ToArray());
    if (compactAddress.Length is not 12 and not 32) {
      throw new ArgumentException(
          "A BLE address must be a 48-bit hardware address or a CoreBluetooth UUID.",
          nameof(address));
    }

    string safeName = new((name ?? "")
        .Where(character => !char.IsControl(character))
        .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' '
            ? character
            : '-')
        .ToArray());
    safeName = string.Join(' ', safeName.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    if (safeName.Length > 40) {
      safeName = safeName[..40];
    }
    if (safeName.Length == 0) {
      safeName = "device";
    }
    return Prefix + safeName + "_" + compactAddress.ToUpperInvariant();
  }

  internal static bool TryAddress(string? endpoint, out string address) {
    address = "";
    if (string.IsNullOrWhiteSpace(endpoint) ||
        !endpoint.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) {
      return false;
    }
    int separator = endpoint.LastIndexOf('_');
    if (separator < Prefix.Length) {
      return false;
    }
    string compact = endpoint[(separator + 1)..];
    if (compact.Length is not 12 and not 32 || !compact.All(Uri.IsHexDigit)) {
      return false;
    }
    compact = compact.ToUpperInvariant();
    address = compact.Length == 12
        ? string.Join(':', Enumerable.Range(0, 6)
            .Select(index => compact.Substring(index * 2, 2)))
        : string.Join('-', compact[..8], compact.Substring(8, 4), compact.Substring(12, 4),
            compact.Substring(16, 4), compact[20..]);
    return true;
  }
}

internal interface IBleUartBackend {
  Task<IReadOnlyList<BleDeviceInfo>> DiscoverAsync(
      TimeSpan duration, CancellationToken cancellationToken);

  Task<IBleUartSession> OpenAsync(
      string address,
      Action<ReadOnlyMemory<byte>> received,
      Action disconnected,
      CancellationToken cancellationToken);
}

internal interface IBleUartSession : IAsyncDisposable {
  bool IsConnected { get; }
  Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
  Task CloseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Cross-platform Mission Planner Nordic-UART BLE serial stream. Platform backends provide the
/// native transport while this layer owns bounded buffering, timeouts and cancellation semantics.
/// </summary>
internal sealed class BleSerial : Stream, ICommsSerial {
  private const int MaximumReceiveBytes = 4 * 1024 * 1024;
  private readonly object _sync = new();
  private readonly Queue<byte> _receive = new();
  private readonly SemaphoreSlim _writeGate = new(1, 1);
  private readonly IBleUartBackend _backend;
  private IBleUartSession? _session;
  private CancellationTokenSource? _openCancellation;
  private Exception? _receiveFailure;
  private bool _isOpen;
  private bool _disposed;

  internal BleSerial(string portName, IBleUartBackend? backend = null) {
    PortName = portName;
    _backend = backend ?? PlatformBackend() ?? throw new PlatformNotSupportedException(
        "Bluetooth LE serial is available on Linux, macOS and Windows.");
  }

  internal static Task<IReadOnlyList<BleDeviceInfo>> DiscoverAsync(
      TimeSpan duration, CancellationToken cancellationToken) {
    IBleUartBackend? backend = PlatformBackend();
    return backend == null
        ? Task.FromResult<IReadOnlyList<BleDeviceInfo>>([])
        : backend.DiscoverAsync(duration, cancellationToken);
  }

  public Stream BaseStream => this;
  public int BaudRate { get; set; } = 115200;
  public int BytesToRead {
    get {
      lock (_sync) {
        return _receive.Count;
      }
    }
  }
  public int BytesToWrite => 0;
  public int DataBits { get; set; } = 8;
  public bool DtrEnable { get; set; }
  public bool IsOpen {
    get {
      lock (_sync) {
        return _isOpen && _session?.IsConnected == true;
      }
    }
  }
  public string PortName { get; set; }
  public int ReadBufferSize { get; set; } = MaximumReceiveBytes;
  public override int ReadTimeout { get; set; } = 1200;
  public bool RtsEnable { get; set; }
  public int WriteBufferSize { get; set; } = 4096;
  public override int WriteTimeout { get; set; } = 1200;

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanTimeout => true;
  public override bool CanWrite => true;
  public override long Length => throw new NotSupportedException();
  public override long Position {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  public void Open() {
    if (!OperatingSystem.IsLinux() && _backend is LinuxBluezBleBackend) {
      throw new PlatformNotSupportedException("The BlueZ BLE transport is available only on Linux.");
    }
    if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows() &&
        _backend is NativeSimpleBleBackend) {
      throw new PlatformNotSupportedException(
          "The native SimpleBLE transport is available only on macOS and Windows.");
    }
    if (!BleEndpoint.TryAddress(PortName, out string address)) {
      throw new IOException($"Invalid BLE endpoint '{PortName}'. Refresh the port list and select a BLE device.");
    }

    bool closeStaleSession;
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (_isOpen && _session?.IsConnected == true) {
        return;
      }
      closeStaleSession = _session != null || _openCancellation != null;
    }
    if (closeStaleSession) {
      Close();
    }

    CancellationTokenSource cancellation;
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      _receive.Clear();
      _receiveFailure = null;
      cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
      _openCancellation = cancellation;
    }

    IBleUartSession? opened = null;
    try {
      opened = _backend.OpenAsync(address, Receive, OnDisconnected, cancellation.Token)
          .GetAwaiter().GetResult();
      if (!opened.IsConnected) {
        throw new IOException($"BLE device {address} disconnected while it was opening.");
      }
      lock (_sync) {
        if (_disposed || cancellation.IsCancellationRequested ||
            !ReferenceEquals(_openCancellation, cancellation)) {
          throw new OperationCanceledException("BLE connection was cancelled.");
        }
        _session = opened;
        _isOpen = true;
        opened = null;
        Monitor.PulseAll(_sync);
      }
    } catch (OperationCanceledException ex) {
      throw new IOException($"Timed out or cancelled while opening BLE device {address}.", ex);
    } finally {
      if (opened != null) {
        try {
          opened.CloseAsync(CancellationToken.None)
              .WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        } catch {
        }
        try {
          opened.DisposeAsync().AsTask().GetAwaiter().GetResult();
        } catch {
        }
      }
      lock (_sync) {
        if (ReferenceEquals(_openCancellation, cancellation)) {
          _openCancellation = null;
        }
      }
      cancellation.Dispose();
    }
  }

  public override void Close() {
    IBleUartSession? session;
    CancellationTokenSource? opening;
    lock (_sync) {
      _isOpen = false;
      opening = _openCancellation;
      _openCancellation = null;
      session = _session;
      _session = null;
      Monitor.PulseAll(_sync);
    }
    try {
      opening?.Cancel();
    } catch (ObjectDisposedException) {
    }
    if (session == null) {
      return;
    }
    try {
      using var closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
      session.CloseAsync(closeCancellation.Token).GetAwaiter().GetResult();
    } catch {
    }
    try {
      session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    } catch {
    }
  }

  public void DiscardInBuffer() {
    lock (_sync) {
      _receive.Clear();
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    ArgumentOutOfRangeException.ThrowIfNegative(offset);
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    if (offset > buffer.Length - count) {
      throw new ArgumentException("Offset and count exceed the destination buffer.");
    }
    if (count == 0) {
      return 0;
    }

    lock (_sync) {
      DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1, ReadTimeout));
      while (_receive.Count == 0 && _receiveFailure == null && _isOpen) {
        TimeSpan remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero || !Monitor.Wait(_sync, remaining)) {
          throw new TimeoutException("No data arrived from the BLE device.");
        }
      }
      if (_receiveFailure != null) {
        throw new IOException("The BLE receive stream failed.", _receiveFailure);
      }
      if (_receive.Count == 0) {
        return 0;
      }
      int read = Math.Min(count, _receive.Count);
      for (int index = 0; index < read; index++) {
        buffer[offset + index] = _receive.Dequeue();
      }
      return read;
    }
  }

  public override int ReadByte() {
    var one = new byte[1];
    return Read(one, 0, 1) == 1 ? one[0] : -1;
  }

  public int ReadChar() => ReadByte();

  public string ReadExisting() {
    int available = BytesToRead;
    if (available == 0) {
      return "";
    }
    var data = new byte[available];
    int read = Read(data, 0, data.Length);
    return Encoding.ASCII.GetString(data, 0, read);
  }

  public string ReadLine() {
    var result = new List<byte>();
    while (true) {
      int value = ReadByte();
      if (value < 0 || value == '\n') {
        break;
      }
      result.Add((byte)value);
    }
    return Encoding.ASCII.GetString(result.ToArray());
  }

  public override void Write(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    ArgumentOutOfRangeException.ThrowIfNegative(offset);
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    if (offset > buffer.Length - count) {
      throw new ArgumentException("Offset and count exceed the source buffer.");
    }
    if (count == 0) {
      return;
    }

    IBleUartSession session;
    lock (_sync) {
      if (!_isOpen || _session?.IsConnected != true) {
        throw new IOException("The BLE transport is closed.");
      }
      session = _session;
    }

    int timeout = Math.Max(1, WriteTimeout);
    if (!_writeGate.Wait(timeout)) {
      throw new TimeoutException("Another BLE write did not finish in time.");
    }
    try {
      byte[] exact = buffer.AsSpan(offset, count).ToArray();
      using var cancellation = new CancellationTokenSource(timeout);
      session.WriteAsync(exact, cancellation.Token).GetAwaiter().GetResult();
    } catch (OperationCanceledException ex) {
      throw new TimeoutException("BLE write timed out.", ex);
    } finally {
      _writeGate.Release();
    }
  }

  public void Write(string text) {
    byte[] bytes = Encoding.ASCII.GetBytes(text);
    Write(bytes, 0, bytes.Length);
  }

  public void WriteLine(string text) => Write(text + "\n");
  public void toggleDTR() { }
  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();

  protected override void Dispose(bool disposing) {
    if (disposing) {
      lock (_sync) {
        if (_disposed) {
          base.Dispose(disposing);
          return;
        }
        _disposed = true;
      }
      Close();
    }
    base.Dispose(disposing);
  }

  private void Receive(ReadOnlyMemory<byte> data) {
    lock (_sync) {
      if (_disposed || (!_isOpen && _session == null && _openCancellation == null)) {
        return;
      }
      if (_receive.Count > MaximumReceiveBytes - data.Length) {
        _receiveFailure = new IOException(
            $"BLE receive buffer exceeded {MaximumReceiveBytes} bytes.");
        _isOpen = false;
        Monitor.PulseAll(_sync);
        return;
      }
      foreach (byte value in data.Span) {
        _receive.Enqueue(value);
      }
      Monitor.PulseAll(_sync);
    }
  }

  private void OnDisconnected() {
    lock (_sync) {
      _isOpen = false;
      Monitor.PulseAll(_sync);
    }
  }

  private static IBleUartBackend? PlatformBackend() {
    if (OperatingSystem.IsLinux()) {
      return LinuxBluezBleBackend.Instance;
    }
    if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) {
      return NativeSimpleBleBackend.Instance;
    }
    return null;
  }
}

internal sealed class LinuxBluezBleBackend : IBleUartBackend {
  internal const string ServiceUuid = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
  internal const string WriteUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
  internal const string NotifyUuid = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";
  internal static LinuxBluezBleBackend Instance { get; } = new();

  private readonly object _adapterSync = new();
  private readonly SemaphoreSlim _scanGate = new(1, 1);
  private Task<IReadOnlyList<Adapter>>? _adapterTask;

  private LinuxBluezBleBackend() { }

  public async Task<IReadOnlyList<BleDeviceInfo>> DiscoverAsync(
      TimeSpan duration, CancellationToken cancellationToken) {
    if (!OperatingSystem.IsLinux()) {
      return [];
    }
    await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      IReadOnlyList<Adapter> adapters = await GetAdaptersAsync(cancellationToken)
          .ConfigureAwait(false);
      if (adapters.Count == 0) {
        return [];
      }

      var started = new List<Adapter>();
      try {
        foreach (Adapter adapter in adapters) {
          cancellationToken.ThrowIfCancellationRequested();
          AdapterProperties properties = await adapter.GetPropertiesAsync()
              .WaitAsync(cancellationToken).ConfigureAwait(false);
          if (!properties.Powered || properties.Discovering) {
            continue;
          }
          try {
            await adapter.SetDiscoveryFilterAsync(new Dictionary<string, object> {
              ["Transport"] = "le",
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            await adapter.StartDiscoveryAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            started.Add(adapter);
          } catch (Exception ex) when (IsAlreadyDiscovering(ex)) {
            // Another Bluetooth client owns the active scan. Its results still arrive through BlueZ.
          }
        }

        if (duration > TimeSpan.Zero) {
          await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        var devices = new Dictionary<string, BleDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (Adapter adapter in adapters) {
          IReadOnlyList<Device> found = await adapter.GetDevicesAsync()
              .WaitAsync(cancellationToken).ConfigureAwait(false);
          foreach (Device device in found) {
            try {
              DeviceProperties properties = await device.GetPropertiesAsync()
                  .WaitAsync(cancellationToken).ConfigureAwait(false);
              if (string.IsNullOrWhiteSpace(properties.Address) ||
                  properties.UUIDs?.Any(uuid =>
                      string.Equals(uuid, ServiceUuid, StringComparison.OrdinalIgnoreCase)) != true) {
                continue;
              }
              string? name = string.IsNullOrWhiteSpace(properties.Alias)
                  ? properties.Name
                  : properties.Alias;
              devices[properties.Address] = new BleDeviceInfo(
                  name ?? "BLE device", properties.Address,
                  BleEndpoint.Create(name, properties.Address));
            } finally {
              device.Dispose();
            }
          }
        }
        return devices.Values.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Address, StringComparer.OrdinalIgnoreCase).ToArray();
      } finally {
        foreach (Adapter adapter in started) {
          try {
            await adapter.StopDiscoveryAsync().WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
          } catch {
          }
        }
      }
    } finally {
      _scanGate.Release();
    }
  }

  public async Task<IBleUartSession> OpenAsync(
      string address,
      Action<ReadOnlyMemory<byte>> received,
      Action disconnected,
      CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(address);
    Device? device = await FindDeviceAsync(address, cancellationToken).ConfigureAwait(false);
    if (device == null) {
      await DiscoverAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
      device = await FindDeviceAsync(address, cancellationToken).ConfigureAwait(false);
    }
    if (device == null) {
      throw new IOException($"BLE device {address} was not found. Ensure it is powered and in range.");
    }

    GattCharacteristic? write = null;
    GattCharacteristic? notify = null;
    IDisposable? notifyWatcher = null;
    IDisposable? connectionWatcher = null;
    try {
      bool connected = await device.GetAsync<bool>("Connected")
          .WaitAsync(cancellationToken).ConfigureAwait(false);
      if (!connected) {
        await device.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
      }
      await WaitForDevicePropertyAsync(
          device, "Connected", true, TimeSpan.FromSeconds(10), cancellationToken)
          .ConfigureAwait(false);
      await WaitForDevicePropertyAsync(
          device, "ServicesResolved", true, TimeSpan.FromSeconds(10), cancellationToken)
          .ConfigureAwait(false);

      IGattService1? service = await device.GetServiceAsync(ServiceUuid)
          .WaitAsync(cancellationToken).ConfigureAwait(false);
      if (service == null) {
        throw new IOException($"BLE device {address} does not expose Nordic UART service {ServiceUuid}.");
      }
      write = await service.GetCharacteristicAsync(WriteUuid)
          .WaitAsync(cancellationToken).ConfigureAwait(false);
      notify = await service.GetCharacteristicAsync(NotifyUuid)
          .WaitAsync(cancellationToken).ConfigureAwait(false);
      if (write == null || notify == null) {
        throw new IOException($"BLE device {address} has an incomplete Nordic UART service.");
      }

      string[] flags = await write.GetFlagsAsync()
          .WaitAsync(cancellationToken).ConfigureAwait(false);
      bool supportsCommand = flags.Contains(
          "write-without-response", StringComparer.OrdinalIgnoreCase);
      bool supportsRequest = flags.Contains("write", StringComparer.OrdinalIgnoreCase);
      if (!supportsCommand && !supportsRequest) {
        throw new IOException($"BLE device {address} has a read-only Nordic UART RX characteristic.");
      }
      string writeType = supportsCommand ? "command" : "request";
      var connectionState = new BleSessionConnection(disconnected);
      notifyWatcher = await notify.WatchPropertiesAsync(changes => {
        object? value = changes.Changed
            .FirstOrDefault(pair => pair.Key == "Value").Value;
        if (value is byte[] bytes && bytes.Length > 0) {
          received(bytes);
        }
      }).WaitAsync(cancellationToken).ConfigureAwait(false);
      connectionWatcher = await device.WatchPropertiesAsync(changes => {
        object? value = changes.Changed
            .FirstOrDefault(pair => pair.Key == "Connected").Value;
        if (false.Equals(value)) {
          connectionState.Disconnect();
        }
      }).WaitAsync(cancellationToken).ConfigureAwait(false);
      await notify.StartNotifyAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

      return new LinuxBluezBleSession(
          device, write, notify, notifyWatcher, connectionWatcher, writeType, connectionState);
    } catch {
      notifyWatcher?.Dispose();
      connectionWatcher?.Dispose();
      notify?.Dispose();
      write?.Dispose();
      try {
        if (await device.GetAsync<bool>("Connected").ConfigureAwait(false)) {
          await device.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
      } catch {
      }
      device.Dispose();
      throw;
    }
  }

  private async Task<Device?> FindDeviceAsync(
      string address, CancellationToken cancellationToken) {
    IReadOnlyList<Adapter> adapters = await GetAdaptersAsync(cancellationToken)
        .ConfigureAwait(false);
    foreach (Adapter adapter in adapters) {
      Device? device = await adapter.GetDeviceAsync(address)
          .WaitAsync(cancellationToken).ConfigureAwait(false);
      if (device != null) {
        return device;
      }
    }
    return null;
  }

  private async Task<IReadOnlyList<Adapter>> GetAdaptersAsync(
      CancellationToken cancellationToken) {
    Task<IReadOnlyList<Adapter>> adapterTask;
    lock (_adapterSync) {
      adapterTask = _adapterTask ??= BlueZManager.GetAdaptersAsync();
    }
    try {
      return await adapterTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    } catch {
      if (adapterTask.IsFaulted) {
        lock (_adapterSync) {
          if (ReferenceEquals(_adapterTask, adapterTask)) {
            _adapterTask = null;
          }
        }
      }
      throw;
    }
  }

  private static async Task WaitForDevicePropertyAsync(
      Device device,
      string property,
      bool expected,
      TimeSpan timeout,
      CancellationToken cancellationToken) {
    DateTime deadline = DateTime.UtcNow + timeout;
    while (await device.GetAsync<bool>(property).WaitAsync(cancellationToken).ConfigureAwait(false)
           != expected) {
      if (DateTime.UtcNow >= deadline) {
        throw new TimeoutException($"Timed out waiting for BLE property {property}={expected}.");
      }
      await Task.Delay(50, cancellationToken).ConfigureAwait(false);
    }
  }

  private static bool IsAlreadyDiscovering(Exception exception) {
    string text = exception.ToString();
    return text.Contains("InProgress", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("already", StringComparison.OrdinalIgnoreCase);
  }
}

internal sealed class LinuxBluezBleSession : IBleUartSession {
  private const int SafeGattPayload = 20;
  private readonly Device _device;
  private readonly GattCharacteristic _write;
  private readonly GattCharacteristic _notify;
  private readonly IDisposable _notifyWatcher;
  private readonly IDisposable _connectionWatcher;
  private readonly string _writeType;
  private readonly BleSessionConnection _connection;
  private int _closed;

  internal LinuxBluezBleSession(
      Device device,
      GattCharacteristic write,
      GattCharacteristic notify,
      IDisposable notifyWatcher,
      IDisposable connectionWatcher,
      string writeType,
      BleSessionConnection connection) {
    _device = device;
    _write = write;
    _notify = notify;
    _notifyWatcher = notifyWatcher;
    _connectionWatcher = connectionWatcher;
    _writeType = writeType;
    _connection = connection;
  }

  public bool IsConnected => _connection.IsConnected;

  public async Task WriteAsync(
      ReadOnlyMemory<byte> data, CancellationToken cancellationToken) {
    if (!IsConnected) {
      throw new IOException("The BLE device is disconnected.");
    }
    for (int offset = 0; offset < data.Length; offset += SafeGattPayload) {
      cancellationToken.ThrowIfCancellationRequested();
      int count = Math.Min(SafeGattPayload, data.Length - offset);
      byte[] chunk = data.Slice(offset, count).ToArray();
      await _write.WriteValueAsync(chunk, new Dictionary<string, object> {
        ["type"] = _writeType,
      }).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  public async Task CloseAsync(CancellationToken cancellationToken) {
    if (Interlocked.Exchange(ref _closed, 1) != 0) {
      return;
    }
    _connection.Disconnect();
    _notifyWatcher.Dispose();
    _connectionWatcher.Dispose();
    try {
      await _notify.StopNotifyAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    } catch {
    }
    try {
      if (await _device.GetAsync<bool>("Connected").WaitAsync(cancellationToken)
          .ConfigureAwait(false)) {
        await _device.DisconnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
      }
    } catch {
    }
  }

  public async ValueTask DisposeAsync() {
    if (Volatile.Read(ref _closed) == 0) {
      try {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await CloseAsync(cancellation.Token).ConfigureAwait(false);
      } catch {
      }
    }
    _notify.Dispose();
    _write.Dispose();
    _device.Dispose();
  }
}

internal sealed class BleSessionConnection {
  private readonly Action _disconnected;
  private int _connected = 1;

  internal BleSessionConnection(Action disconnected) => _disconnected = disconnected;

  internal bool IsConnected => Volatile.Read(ref _connected) != 0;

  internal void Disconnect() {
    if (Interlocked.Exchange(ref _connected, 0) != 0) {
      _disconnected();
    }
  }
}
