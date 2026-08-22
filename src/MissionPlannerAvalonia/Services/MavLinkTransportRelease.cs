using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.Comms;

namespace MissionPlannerAvalonia.Services;

/// <summary>
/// Releases a lost MAVLink transport without making the UI wait for an OS/driver Close call.
/// The captured stream is the only object touched by the background cleanup, so a later
/// connection cannot be closed accidentally when the old cleanup eventually completes.
/// </summary>
internal sealed class MavLinkTransportRelease {
  private readonly object _sync = new();
  private ICommsSerial? _stream;
  private Task<bool>? _release;

  internal Task<bool> Begin(MAVLinkInterface link) {
    ArgumentNullException.ThrowIfNull(link);
    lock (_sync) {
      ICommsSerial? current = link.BaseStream;
      if (ReferenceEquals(current, ClosedMavLinkTransport.Instance)) {
        return _release ?? Task.FromResult(true);
      }
      if (ReferenceEquals(_stream, current) && _release != null) {
        return _release;
      }

      // Make every MAVLink loop observe the logical shutdown before a possibly blocking driver
      // close begins. Atomically replace the upstream private transport field: its public setter
      // closes the previous stream synchronously and would reproduce the unplug deadlock. The
      // closed sentinel also makes a later public BaseStream assignment safe and immediate.
      link.giveComport = false;
      MissionPlanner.Utilities.TerrainFollow? terrain = link.Terrain;
      link.Terrain = null;
      ICommsSerial? stream = MavLinkBaseStreamAccess.Detach(
          link, current, ClosedMavLinkTransport.Instance);
      _stream = stream;
      _release = Task.Run(() => Release(stream, terrain));
      return _release;
    }
  }

  internal async Task<bool> WaitForCurrentAsync(
      MAVLinkInterface link,
      TimeSpan timeout,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(link);
    if (timeout <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    ICommsSerial? current = link.BaseStream;
    Task<bool>? release;
    lock (_sync) {
      release = ReferenceEquals(_stream, current) ? _release : null;
    }
    if (release == null) {
      if (!IsOpen(current)) {
        return true;
      }
      release = Begin(link);
    }

    try {
      return await release.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    } catch (TimeoutException) {
      return false;
    }
  }

  private static bool Release(
      ICommsSerial? stream,
      MissionPlanner.Utilities.TerrainFollow? terrain) {
    try {
      terrain?.UnSub();
    } catch {
    }
    if (stream == null) {
      return true;
    }
    try {
      if (IsOpen(stream)) {
        stream.Close();
      }
    } catch {
    }
    try {
      stream.Dispose();
    } catch {
    }
    return !IsOpen(stream);
  }

  private static bool IsOpen(ICommsSerial? stream) {
    try {
      return stream?.IsOpen == true;
    } catch {
      return false;
    }
  }

  private static class MavLinkBaseStreamAccess {
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_baseStream")]
    private static extern ref ICommsSerial? GetField(MAVLinkInterface link);

    internal static ICommsSerial? Detach(
        MAVLinkInterface link, ICommsSerial? expected, ICommsSerial replacement) {
      try {
        ref ICommsSerial? field = ref GetField(link);
        Interlocked.CompareExchange(ref field, replacement, expected);
      } catch (Exception) {
        // Upstream compatibility fallback: if Mission Planner ever renames the private field,
        // cleanup is still asynchronous. Reconnect then waits for that old stream to finish.
      }
      return expected;
    }
  }

  /// <summary>
  /// A permanently closed placeholder used while a captured OS transport finishes releasing.
  /// It deliberately refuses Open so a stale upstream worker cannot resurrect the old session.
  /// </summary>
  private sealed class ClosedMavLinkTransport : ICommsSerial {
    internal static ClosedMavLinkTransport Instance { get; } = new();

    private ClosedMavLinkTransport() { }

    public Stream BaseStream => Stream.Null;
    public int BaudRate { get; set; }
    public int BytesToRead => 0;
    public int BytesToWrite => 0;
    public int DataBits { get; set; }
    public bool DtrEnable { get; set; }
    public bool IsOpen => false;
    public string PortName { get; set; } = "";
    public int ReadBufferSize { get; set; }
    public int ReadTimeout { get; set; }
    public bool RtsEnable { get; set; }
    public int WriteBufferSize { get; set; }
    public int WriteTimeout { get; set; }

    public void Close() { }
    public void DiscardInBuffer() { }
    public void Dispose() { }
    public void Open() => throw new IOException("The MAVLink transport has been detached.");
    public int Read(byte[] buffer, int offset, int count) => 0;
    public int ReadByte() => -1;
    public int ReadChar() => -1;
    public string ReadExisting() => "";
    public string ReadLine() => "";
    public void Write(string text) => throw new IOException("The MAVLink transport is closed.");
    public void Write(byte[] buffer, int offset, int count) =>
        throw new IOException("The MAVLink transport is closed.");
    public void WriteLine(string text) => throw new IOException("The MAVLink transport is closed.");
    public void toggleDTR() { }
  }
}
