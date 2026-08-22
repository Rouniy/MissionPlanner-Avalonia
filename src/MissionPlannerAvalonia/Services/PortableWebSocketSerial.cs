using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Comms;

namespace MissionPlannerAvalonia.Services;

/// <summary>
/// A bounded, cancellable byte-stream adapter for Mission Planner's WebSocket connection option.
/// The upstream transport starts an async-void reader, cannot wait for its shutdown and sends a
/// Socket.IO probe to every raw WebSocket endpoint. This implementation owns one receive loop and
/// one serialized send path for the complete lifetime of the selected connection.
/// </summary>
internal sealed class PortableWebSocketSerial : Stream, ICommsSerial,
    MissionPlannerAvalonia.ViewModels.IPreconfiguredNetworkStream {
  private const int MaximumReceiveBytes = 8 * 1024 * 1024;
  private const int MaximumTextMessageBytes = 1024 * 1024;
  private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
  private readonly object _sync = new();
  private readonly Queue<byte> _receive = new();
  private readonly SemaphoreSlim _writeGate = new(1, 1);
  private readonly Uri _endpoint;
  private readonly NetworkCredential? _credentials;
  private readonly bool _socketIo;
  private ClientWebSocket? _client;
  private CancellationTokenSource? _lifetimeCancellation;
  private Task? _receiveTask;
  private Exception? _receiveFailure;
  private bool _isOpen;
  private bool _disposed;
  private long _generation;

  internal PortableWebSocketSerial(string endpoint) {
    (_endpoint, _credentials) = NormalizeEndpoint(endpoint);
    _socketIo = IsSocketIoEndpoint(_endpoint);
  }

  internal bool AutoReconnect { get; set; } = true;
  internal int ReconnectAttempts { get; set; } = 3;
  internal Uri Endpoint => _endpoint;
  internal bool UsesSocketIo => _socketIo;
  public bool SuppressesUpstreamInput => true;

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
        // Remain logically open during bounded reconnect attempts so Mission Planner's outer
        // connection monitor does not tear down this transport before the first retry begins.
        return _isOpen;
      }
    }
  }
  public string PortName { get; set; } = "WS";
  public int ReadBufferSize { get; set; } = MaximumReceiveBytes;
  public override int ReadTimeout { get; set; } = 500;
  public bool RtsEnable { get; set; }
  public int WriteBufferSize { get; set; } = 8192;
  public override int WriteTimeout { get; set; } = 2000;

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
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (_isOpen) {
        return;
      }
    }

    CloseTransport(waitForReader: true);

    CancellationTokenSource lifetime;
    long generation;
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      _receive.Clear();
      _receiveFailure = null;
      lifetime = new CancellationTokenSource();
      _lifetimeCancellation = lifetime;
      generation = ++_generation;
    }

    ClientWebSocket? opened = null;
    try {
      opened = ConnectAsync(lifetime.Token, OpenTimeout).GetAwaiter().GetResult();
      lock (_sync) {
        if (_disposed || lifetime.IsCancellationRequested ||
            !ReferenceEquals(_lifetimeCancellation, lifetime) || generation != _generation) {
          throw new OperationCanceledException("The WebSocket connection was cancelled.");
        }
        _client = opened;
        _isOpen = true;
        Monitor.PulseAll(_sync);
        ClientWebSocket active = opened;
        opened = null;
        _receiveTask = RunLifetimeAsync(active, lifetime, generation);
      }
    } catch (OperationCanceledException ex) {
      CleanupFailedOpen(lifetime, generation);
      throw new IOException($"Timed out or cancelled while opening {_endpoint.Host}.", ex);
    } catch (WebSocketException ex) {
      CleanupFailedOpen(lifetime, generation);
      throw new IOException($"Could not open WebSocket {_endpoint.Host}: {ex.Message}", ex);
    } catch {
      CleanupFailedOpen(lifetime, generation);
      throw;
    } finally {
      opened?.Dispose();
    }
  }

  public override void Close() => CloseTransport(waitForReader: true);

  public void DiscardInBuffer() {
    lock (_sync) {
      _receive.Clear();
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ValidateBuffer(buffer, offset, count);
    if (count == 0) {
      return 0;
    }

    lock (_sync) {
      DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1, ReadTimeout));
      while (_receive.Count == 0 && _receiveFailure == null && _isOpen) {
        TimeSpan remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero || !Monitor.Wait(_sync, remaining)) {
          throw new TimeoutException("No data arrived from the WebSocket endpoint.");
        }
      }
      if (_receiveFailure != null) {
        throw new IOException("The WebSocket receive stream failed.", _receiveFailure);
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
    ValidateBuffer(buffer, offset, count);
    if (count == 0) {
      return;
    }

    ClientWebSocket client;
    CancellationToken lifetimeToken;
    lock (_sync) {
      if (!_isOpen || _client?.State != WebSocketState.Open ||
          _lifetimeCancellation == null) {
        throw new IOException("The WebSocket transport is closed.");
      }
      client = _client;
      lifetimeToken = _lifetimeCancellation.Token;
    }

    int timeout = Math.Max(1, WriteTimeout);
    if (!_writeGate.Wait(timeout)) {
      throw new TimeoutException("Another WebSocket write did not finish in time.");
    }
    try {
      byte[] exact;
      if (_socketIo) {
        exact = new byte[count + 1];
        exact[0] = 4;
        Buffer.BlockCopy(buffer, offset, exact, 1, count);
      } else {
        exact = buffer.AsSpan(offset, count).ToArray();
      }
      using var sendCancellation =
          CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
      sendCancellation.CancelAfter(timeout);
      client.SendAsync(new ArraySegment<byte>(exact), WebSocketMessageType.Binary, true,
          sendCancellation.Token).GetAwaiter().GetResult();
    } catch (OperationCanceledException ex) when (!lifetimeToken.IsCancellationRequested) {
      throw new TimeoutException("WebSocket write timed out.", ex);
    } catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or
                                 OperationCanceledException) {
      throw new IOException("The WebSocket write failed because the connection closed.", ex);
    } finally {
      _writeGate.Release();
    }
  }

  public void Write(string text) {
    byte[] data = Encoding.ASCII.GetBytes(text);
    Write(data, 0, data.Length);
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
      CloseTransport(waitForReader: true);
    }
    base.Dispose(disposing);
  }

  internal static (Uri Endpoint, NetworkCredential? Credentials) NormalizeEndpoint(string value) {
    if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? source)) {
      throw new ArgumentException("Enter an absolute ws:// or wss:// URL.", nameof(value));
    }
    string scheme = source.Scheme.ToLowerInvariant() switch {
      "ws" => "ws",
      "wss" => "wss",
      "http" => "ws",
      "https" => "wss",
      _ => throw new ArgumentException("WebSocket URLs must use ws:// or wss://.", nameof(value)),
    };

    NetworkCredential? credentials = null;
    if (!string.IsNullOrEmpty(source.UserInfo)) {
      string[] parts = source.UserInfo.Split(':', 2);
      credentials = new NetworkCredential(
          Uri.UnescapeDataString(parts[0]),
          parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "");
    }
    var builder = new UriBuilder(source) {
      Scheme = scheme,
      Port = source.IsDefaultPort ? -1 : source.Port,
      UserName = "",
      Password = "",
    };
    return (builder.Uri, credentials);
  }

  internal static bool IsSocketIoEndpoint(Uri endpoint) =>
      endpoint.AbsolutePath.Contains("socket.io", StringComparison.OrdinalIgnoreCase) ||
      endpoint.Query.Contains("EIO=", StringComparison.OrdinalIgnoreCase);

  private async Task<ClientWebSocket> ConnectAsync(
      CancellationToken lifetimeToken, TimeSpan timeout) {
    var client = new ClientWebSocket();
    client.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    if (_credentials != null) {
      client.Options.Credentials = _credentials;
    }
    using var connectCancellation =
        CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
    connectCancellation.CancelAfter(timeout);
    try {
      await client.ConnectAsync(_endpoint, connectCancellation.Token).ConfigureAwait(false);
      return client;
    } catch {
      client.Dispose();
      throw;
    }
  }

  private async Task RunLifetimeAsync(
      ClientWebSocket initial,
      CancellationTokenSource lifetime,
      long generation) {
    ClientWebSocket client = initial;
    Exception? lastFailure = null;
    try {
      while (!lifetime.IsCancellationRequested) {
        bool receiveLimitReached = false;
        try {
          await ReceiveSessionAsync(client, lifetime.Token).ConfigureAwait(false);
          lastFailure = new IOException("The remote WebSocket endpoint closed the connection.");
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
          return;
        } catch (ReceiveBufferLimitException ex) {
          lastFailure = ex;
          receiveLimitReached = true;
        } catch (Exception ex) when (ex is WebSocketException or IOException or
                                     ObjectDisposedException or OperationCanceledException) {
          lastFailure = ex;
        }

        MarkSessionClosed(client, generation);
        client.Dispose();
        if (receiveLimitReached || !AutoReconnect || ReconnectAttempts <= 0 ||
            lifetime.IsCancellationRequested) {
          break;
        }

        ClientWebSocket? replacement = null;
        int maximumAttempts = Math.Max(0, ReconnectAttempts);
        for (int attempt = 1; attempt <= maximumAttempts; attempt++) {
          try {
            int shift = Math.Min(attempt - 1, 4);
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(2000, 200 * (1 << shift)));
            await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
            replacement = await ConnectAsync(lifetime.Token, OpenTimeout).ConfigureAwait(false);
            lock (_sync) {
              if (_disposed || lifetime.IsCancellationRequested || generation != _generation ||
                  !ReferenceEquals(_lifetimeCancellation, lifetime)) {
                replacement.Dispose();
                return;
              }
              _client = replacement;
              _isOpen = true;
              _receiveFailure = null;
              Monitor.PulseAll(_sync);
            }
            break;
          } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
            return;
          } catch (Exception ex) when (ex is WebSocketException or IOException or
                                       ObjectDisposedException or OperationCanceledException) {
            replacement?.Dispose();
            replacement = null;
            lastFailure = ex;
          }
        }
        if (replacement == null) {
          break;
        }
        client = replacement;
      }
    } finally {
      MarkSessionClosed(client, generation);
      client.Dispose();
      lock (_sync) {
        if (generation == _generation && ReferenceEquals(_lifetimeCancellation, lifetime)) {
          _client = null;
          _isOpen = false;
          _receiveFailure = lifetime.IsCancellationRequested ? null : lastFailure;
          Monitor.PulseAll(_sync);
        }
      }
    }
  }

  private void CleanupFailedOpen(CancellationTokenSource lifetime, long generation) {
    lock (_sync) {
      if (generation == _generation && ReferenceEquals(_lifetimeCancellation, lifetime)) {
        _isOpen = false;
        _lifetimeCancellation = null;
        _generation++;
        Monitor.PulseAll(_sync);
      }
    }
    try {
      lifetime.Cancel();
    } catch (ObjectDisposedException) {
    }
    lifetime.Dispose();
  }

  private async Task ReceiveSessionAsync(
      ClientWebSocket client, CancellationToken cancellationToken) {
    var buffer = new byte[8192];
    using var text = new MemoryStream();
    while (client.State is WebSocketState.Open or WebSocketState.CloseSent) {
      WebSocketReceiveResult result = await client.ReceiveAsync(
          new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
      if (result.MessageType == WebSocketMessageType.Close) {
        return;
      }
      if (result.MessageType == WebSocketMessageType.Binary) {
        Enqueue(buffer.AsSpan(0, result.Count));
        continue;
      }
      if (result.MessageType != WebSocketMessageType.Text) {
        continue;
      }
      if (text.Length > MaximumTextMessageBytes - result.Count) {
        throw new ReceiveBufferLimitException("WebSocket text message exceeded 1 MiB.");
      }
      text.Write(buffer, 0, result.Count);
      if (!result.EndOfMessage) {
        continue;
      }
      if (_socketIo) {
        await HandleSocketIoTextAsync(client, Encoding.UTF8.GetString(text.ToArray()),
            cancellationToken).ConfigureAwait(false);
      }
      text.SetLength(0);
    }
  }

  private async Task HandleSocketIoTextAsync(
      ClientWebSocket client, string message, CancellationToken cancellationToken) {
    string? response = message.StartsWith('0')
        ? "2probe"
        : message.StartsWith("3probe", StringComparison.Ordinal) || message == "3"
            ? "5"
            : null;
    if (response == null) {
      return;
    }
    await SendTextAsync(client, response, cancellationToken).ConfigureAwait(false);
    if (response == "5") {
      await SendTextAsync(client, "40/MAVControl,", cancellationToken).ConfigureAwait(false);
    }
  }

  private async Task SendTextAsync(
      ClientWebSocket client, string value, CancellationToken cancellationToken) {
    await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      byte[] data = Encoding.ASCII.GetBytes(value);
      await client.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true,
          cancellationToken).ConfigureAwait(false);
    } finally {
      _writeGate.Release();
    }
  }

  private void Enqueue(ReadOnlySpan<byte> data) {
    lock (_sync) {
      if (_receive.Count > MaximumReceiveBytes - data.Length) {
        throw new ReceiveBufferLimitException(
            $"WebSocket receive buffer exceeded {MaximumReceiveBytes} bytes.");
      }
      foreach (byte value in data) {
        _receive.Enqueue(value);
      }
      Monitor.PulseAll(_sync);
    }
  }

  private void MarkSessionClosed(ClientWebSocket client, long generation) {
    lock (_sync) {
      if (generation == _generation && ReferenceEquals(_client, client)) {
        _client = null;
        Monitor.PulseAll(_sync);
      }
    }
  }

  private void CloseTransport(bool waitForReader) {
    CancellationTokenSource? lifetime;
    ClientWebSocket? client;
    Task? reader;
    lock (_sync) {
      _isOpen = false;
      lifetime = _lifetimeCancellation;
      _lifetimeCancellation = null;
      client = _client;
      _client = null;
      reader = _receiveTask;
      _receiveTask = null;
      _receiveFailure = null;
      _generation++;
      Monitor.PulseAll(_sync);
    }
    try {
      lifetime?.Cancel();
    } catch (ObjectDisposedException) {
    }
    try {
      client?.Abort();
    } catch {
    }
    if (waitForReader && reader != null && Task.CurrentId != reader.Id) {
      try {
        reader.Wait(TimeSpan.FromSeconds(1));
      } catch {
      }
    }
    client?.Dispose();
    if (lifetime != null) {
      if (reader == null || reader.IsCompleted) {
        lifetime.Dispose();
      } else {
        _ = reader.ContinueWith(completed => {
          _ = completed.Exception;
          lifetime.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
      }
    }
  }

  private static void ValidateBuffer(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    ArgumentOutOfRangeException.ThrowIfNegative(offset);
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    if (offset > buffer.Length - count) {
      throw new ArgumentException("Offset and count exceed the buffer.");
    }
  }

  private sealed class ReceiveBufferLimitException(string message) : IOException(message);
}
