using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MissionPlanner;

namespace MissionPlannerAvalonia.Services;

internal sealed record SerialControlTarget(
    MAVLinkInterface Link, MAVState Vehicle, byte SystemId, byte ComponentId);

internal static class SerialControlTargetGuard {
  internal static bool MatchesSelection(
      SerialControlTarget target, MAVLinkInterface activeLink, MAVState activeVehicle) =>
      ReferenceEquals(target.Link, activeLink)
      && ReferenceEquals(target.Vehicle, activeVehicle)
      && target.SystemId == activeVehicle.sysid
      && target.ComponentId == activeVehicle.compid;

  internal static bool HasSingleSystemTarget(MAVLinkInterface link, byte systemId) {
    try {
      byte[] systems = link.MAVlist.ToArray()
          .Select(vehicle => vehicle.sysid)
          .Where(id => id != 0)
          .Distinct()
          .ToArray();
      return systems.Length == 1 && systems[0] == systemId;
    } catch (InvalidOperationException) {
      // MAV discovery can update the upstream collection concurrently. Fail closed and let the
      // caller retry after the list stabilizes rather than sending an untargeted control packet.
      return false;
    }
  }

  internal static bool IsAutopilotComponent(byte componentId) =>
      componentId == (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1;

  internal static bool IsCurrent(SerialControlTarget target) {
    MAVLinkInterface activeLink = AppState.comPort;
    return activeLink.BaseStream?.IsOpen == true
        && MatchesSelection(target, activeLink, activeLink.MAV)
        && !target.Vehicle.cs.armed
        && HasSingleSystemTarget(target.Link, target.SystemId);
  }

  internal static bool TryCapture(out SerialControlTarget? target, out string error) {
    MAVLinkInterface link = AppState.comPort;
    if (link.BaseStream?.IsOpen != true) {
      target = null;
      error = "Connect and select a vehicle before starting the bridge.";
      return false;
    }

    MAVState vehicle = link.MAV;
    if (vehicle.sysid == 0 || vehicle.compid == 0) {
      target = null;
      error = "Select a concrete MAVLink system and component before starting the bridge.";
      return false;
    }
    if (!IsAutopilotComponent(vehicle.compid)) {
      target = null;
      error = "Select the autopilot component (component 1). SERIAL_CONTROL replies are emitted "
          + "by the autopilot, not a camera or peripheral component.";
      return false;
    }
    if (vehicle.cs.armed) {
      target = null;
      error = "The serial bridge is blocked while the selected vehicle is armed.";
      return false;
    }
    if (!HasSingleSystemTarget(link, vehicle.sysid)) {
      target = null;
      error = "SERIAL_CONTROL has no target-system field. For safety, the bridge requires a "
          + "telemetry link containing exactly one MAVLink system; use a separate Connection List "
          + "link for this device.";
      return false;
    }

    target = new SerialControlTarget(link, vehicle, vehicle.sysid, vehicle.compid);
    error = "";
    return true;
  }
}

internal interface ISerialControlSession : IDisposable {
  event Action<ReadOnlyMemory<byte>>? DataReceived;

  bool IsCurrent { get; }
  string TargetDescription { get; }

  void Open();
  void RequestData();
  void Write(ReadOnlyMemory<byte> data);
  void Close();
}

internal sealed class MavlinkSerialControlSession : ISerialControlSession {
  private const ushort RequestTimeoutMs = 100;
  private readonly SerialControlTarget _target;
  private readonly MAVLink.SERIAL_CONTROL_DEV _device;
  private readonly uint _baudRate;
  private readonly int _subscription;
  private int _open;
  private int _disposed;

  internal MavlinkSerialControlSession(
      SerialControlTarget target, MAVLink.SERIAL_CONTROL_DEV device, uint baudRate) {
    _target = target;
    _device = device;
    _baudRate = baudRate;
    _subscription = target.Link.SubscribeToPacketType(
        MAVLink.MAVLINK_MSG_ID.SERIAL_CONTROL,
        OnSerialControl,
        target.SystemId,
        target.ComponentId);
  }

  public event Action<ReadOnlyMemory<byte>>? DataReceived;

  public bool IsCurrent => Volatile.Read(ref _disposed) == 0
      && SerialControlTargetGuard.IsCurrent(_target);

  public string TargetDescription =>
      $"{_target.SystemId}:{_target.ComponentId} / {_device}";

  public void Open() {
    EnsureCurrent();
    Volatile.Write(ref _open, 1);
    try {
      _target.Link.SendSerialControl(_device, RequestTimeoutMs, null, _baudRate);
    } catch {
      Close();
      throw;
    }
  }

  public void RequestData() {
    EnsureOpenAndCurrent();
    _target.Link.SendSerialControl(_device, RequestTimeoutMs, null);
  }

  public void Write(ReadOnlyMemory<byte> data) {
    EnsureOpenAndCurrent();
    if (!data.IsEmpty) {
      _target.Link.SendSerialControl(_device, 0, data.ToArray());
    }
  }

  public void Close() {
    if (Interlocked.Exchange(ref _open, 0) == 0) {
      return;
    }
    try {
      // A zero-flag SERIAL_CONTROL packet releases the autopilot UART from exclusive mode.
      _target.Link.SendSerialControl(_device, 0, null, 0, close: true);
    } catch {
      // A physically lost link cannot acknowledge the release, but Close must remain bounded.
    }
  }

  private bool OnSerialControl(MAVLink.MAVLinkMessage packet) {
    if (Volatile.Read(ref _disposed) != 0) {
      return true;
    }
    var message = packet.ToStructure<MAVLink.mavlink_serial_control_t>();
    if (message.device != (byte)_device || message.data == null) {
      return true;
    }
    int count = Math.Min(message.count, message.data.Length);
    if (count > 0) {
      var copy = new byte[count];
      Array.Copy(message.data, copy, count);
      DataReceived?.Invoke(copy);
    }
    return true;
  }

  private void EnsureCurrent() {
    if (!IsCurrent) {
      throw new SerialControlTargetChangedException();
    }
  }

  private void EnsureOpenAndCurrent() {
    if (Volatile.Read(ref _open) == 0) {
      throw new InvalidOperationException("The MAVLink serial-control session is not open.");
    }
    EnsureCurrent();
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      return;
    }
    Close();
    _target.Link.UnSubscribeToPacketType(_subscription);
  }
}

internal sealed class SerialControlTargetChangedException : InvalidOperationException {
  internal SerialControlTargetChangedException()
      : base("The active modem/vehicle changed, disconnected, became armed, or the link now "
          + "contains multiple MAVLink systems.") {
  }
}

internal sealed class MavlinkSerialTcpBridge : IAsyncDisposable {
  private const int TcpReadSize = 280;
  private static readonly TimeSpan TargetPollInterval = TimeSpan.FromMilliseconds(100);
  private static readonly TimeSpan SerialPollInterval = TimeSpan.FromMilliseconds(50);
  private readonly ISerialControlSession _serial;
  private readonly TcpListener _listener;
  private readonly CancellationTokenSource _stop = new();
  private readonly object _clientSync = new();
  private TcpClient? _activeClient;
  private Channel<byte[]>? _serialQueue;
  private Task? _runTask;
  private int _started;
  private int _sessionDisposed;
  private long _fromTcp;
  private long _toTcp;
  private long _dropped;

  internal MavlinkSerialTcpBridge(
      ISerialControlSession serial, IPAddress bindAddress, int listenPort) {
    if (listenPort is < 0 or > 65535) {
      throw new ArgumentOutOfRangeException(nameof(listenPort));
    }
    _serial = serial;
    _listener = new TcpListener(bindAddress, listenPort);
  }

  internal event Action<string>? StatusChanged;
  internal event Action? CountersChanged;

  internal int BoundPort { get; private set; }
  internal long BytesFromTcp => Interlocked.Read(ref _fromTcp);
  internal long BytesToTcp => Interlocked.Read(ref _toTcp);
  internal long DroppedSerialBytes => Interlocked.Read(ref _dropped);
  internal Task Completion => _runTask ?? Task.CompletedTask;

  internal void Start() {
    if (Interlocked.Exchange(ref _started, 1) != 0) {
      throw new InvalidOperationException("The bridge has already been started.");
    }
    try {
      _listener.Start(1);
      BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
      _serial.DataReceived += OnSerialData;
      _runTask = RunAsync(_stop.Token);
    } catch {
      DisposeSession();
      throw;
    }
  }

  internal void Cancel() {
    if (!_stop.IsCancellationRequested) {
      _stop.Cancel();
    }
    try {
      _listener.Stop();
    } catch {
    }
    lock (_clientSync) {
      try {
        _activeClient?.Close();
      } catch {
      }
    }
  }

  internal async Task StopAsync() {
    Cancel();
    Task? runTask = _runTask;
    if (runTask != null) {
      try {
        await runTask.ConfigureAwait(false);
      } catch (OperationCanceledException) when (_stop.IsCancellationRequested) {
      } catch (SerialControlTargetChangedException) {
      }
    } else {
      DisposeSession();
    }
  }

  private async Task RunAsync(CancellationToken cancellationToken) {
    RaiseStatus($"Listening on {_listener.LocalEndpoint}; waiting for one TCP client.");
    try {
      while (true) {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTargetCurrent();
        TcpClient client = await AcceptWhileTargetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        using (client) {
          client.NoDelay = true;
          lock (_clientSync) {
            _activeClient = client;
          }
          try {
            try {
              await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
            } catch (IOException ex) when (!cancellationToken.IsCancellationRequested
                && _serial.IsCurrent) {
              RaiseStatus($"TCP client ended ({ex.Message}); listener remains active.");
            } catch (SocketException ex) when (!cancellationToken.IsCancellationRequested
                && _serial.IsCurrent) {
              RaiseStatus($"TCP client ended ({ex.Message}); listener remains active.");
            } catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested
                && _serial.IsCurrent) {
            }
          } finally {
            lock (_clientSync) {
              if (ReferenceEquals(_activeClient, client)) {
                _activeClient = null;
              }
            }
          }
        }
        RaiseStatus($"TCP client disconnected; listening on {_listener.LocalEndpoint}.");
      }
    } finally {
      _serialQueue = null;
      _serial.DataReceived -= OnSerialData;
      try {
        _listener.Stop();
      } catch {
      }
      DisposeSession();
    }
  }

  private async Task<TcpClient> AcceptWhileTargetCurrentAsync(
      CancellationToken cancellationToken) {
    using var acceptStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    Task<TcpClient> acceptTask = _listener.AcceptTcpClientAsync(acceptStop.Token).AsTask();
    try {
      while (!acceptTask.IsCompleted) {
        await Task.WhenAny(
            acceptTask,
            Task.Delay(TargetPollInterval, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTargetCurrent();
      }
      return await acceptTask.ConfigureAwait(false);
    } catch {
      acceptStop.Cancel();
      try {
        await acceptTask.ConfigureAwait(false);
      } catch {
      }
      throw;
    }
  }

  private async Task HandleClientAsync(TcpClient client, CancellationToken bridgeToken) {
    EnsureTargetCurrent();
    var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128) {
      FullMode = BoundedChannelFullMode.Wait,
      SingleReader = true,
      SingleWriter = false,
    });
    _serialQueue = queue;
    using var clientStop = CancellationTokenSource.CreateLinkedTokenSource(bridgeToken);
    NetworkStream stream = client.GetStream();
    _serial.Open();
    RaiseStatus($"TCP client {client.Client.RemoteEndPoint} connected to {_serial.TargetDescription}.");
    Task fromTcp = PumpTcpToSerialAsync(stream, clientStop.Token);
    Task toTcp = PumpSerialToTcpAsync(stream, queue.Reader, clientStop.Token);
    Task poll = PollSerialAsync(clientStop.Token);
    try {
      Task first = await Task.WhenAny(fromTcp, toTcp, poll).ConfigureAwait(false);
      await first.ConfigureAwait(false);
    } finally {
      clientStop.Cancel();
      client.Close();
      _serialQueue = null;
      queue.Writer.TryComplete();
      await ObserveCancelledAsync(fromTcp, toTcp, poll).ConfigureAwait(false);
      _serial.Close();
    }
  }

  private async Task PumpTcpToSerialAsync(NetworkStream stream, CancellationToken token) {
    var buffer = new byte[TcpReadSize];
    while (true) {
      int count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
      if (count == 0) {
        return;
      }
      EnsureTargetCurrent();
      _serial.Write(buffer.AsMemory(0, count));
      Interlocked.Add(ref _fromTcp, count);
      CountersChanged?.Invoke();
    }
  }

  private async Task PumpSerialToTcpAsync(
      NetworkStream stream, ChannelReader<byte[]> reader, CancellationToken token) {
    await foreach (byte[] data in reader.ReadAllAsync(token).ConfigureAwait(false)) {
      await stream.WriteAsync(data, token).ConfigureAwait(false);
      Interlocked.Add(ref _toTcp, data.Length);
      CountersChanged?.Invoke();
    }
  }

  private async Task PollSerialAsync(CancellationToken token) {
    while (true) {
      token.ThrowIfCancellationRequested();
      EnsureTargetCurrent();
      _serial.RequestData();
      await Task.Delay(SerialPollInterval, token).ConfigureAwait(false);
    }
  }

  private void OnSerialData(ReadOnlyMemory<byte> data) {
    if (data.IsEmpty) {
      return;
    }
    Channel<byte[]>? queue = _serialQueue;
    if (queue == null || !queue.Writer.TryWrite(data.ToArray())) {
      Interlocked.Add(ref _dropped, data.Length);
      CountersChanged?.Invoke();
    }
  }

  private void EnsureTargetCurrent() {
    if (!_serial.IsCurrent) {
      throw new SerialControlTargetChangedException();
    }
  }

  private static async Task ObserveCancelledAsync(params Task[] tasks) {
    foreach (Task task in tasks) {
      try {
        await task.ConfigureAwait(false);
      } catch (OperationCanceledException) {
      } catch (ObjectDisposedException) {
      } catch (IOException) {
      } catch (SocketException) {
      } catch (SerialControlTargetChangedException) {
      }
    }
  }

  private void RaiseStatus(string status) => StatusChanged?.Invoke(status);

  private void DisposeSession() {
    if (Interlocked.Exchange(ref _sessionDisposed, 1) == 0) {
      _serial.Dispose();
    }
  }

  public async ValueTask DisposeAsync() {
    await StopAsync().ConfigureAwait(false);
    _stop.Dispose();
  }
}
