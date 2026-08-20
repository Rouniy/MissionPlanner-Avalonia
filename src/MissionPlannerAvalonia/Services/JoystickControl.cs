using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.Joystick;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct JoystickOutputSnapshot(
    bool ManualControl,
    bool[] Mapped,
    short[] Values
);

internal static class JoystickControlPackets {
  private const int _channelCount = 18;

  public static JoystickOutputSnapshot Capture(JoystickBase joystick, CurrentState state) {
    var mapped = new bool[_channelCount];
    var values = new short[_channelCount];
    for (int channel = 1; channel <= _channelCount; channel++) {
      mapped[channel - 1] = joystick.getJoystickAxis(channel) != joystickaxis.None;
      values[channel - 1] = GetOverride(state, channel);
    }
    return new JoystickOutputSnapshot(joystick.manual_control, mapped, values);
  }

  public static MAVLink.mavlink_rc_channels_override_t BuildRcOverride(
      byte sysid, byte compid, JoystickOutputSnapshot snapshot) {
    Validate(snapshot);
    ushort Value(int channel) {
      if (!snapshot.Mapped[channel - 1]) {
        // MAVLink uses UINT16_MAX as "ignore" for channels 1-8 and zero for
        // the extension channels. This matches the upstream Mission Planner sender.
        return channel <= 8 ? ushort.MaxValue : (ushort)0;
      }
      if (snapshot.Values[channel - 1] == -1) {
        // joystickaxis.ARx uses -1 as the upstream UINT16_MAX sentinel. Preserve it as 65535;
        // clamping it to zero would release the channel instead of ignoring it.
        return ushort.MaxValue;
      }
      return (ushort)Math.Clamp((int)snapshot.Values[channel - 1], 0, ushort.MaxValue);
    }

    return new MAVLink.mavlink_rc_channels_override_t {
      target_system = sysid,
      target_component = compid,
      chan1_raw = Value(1),
      chan2_raw = Value(2),
      chan3_raw = Value(3),
      chan4_raw = Value(4),
      chan5_raw = Value(5),
      chan6_raw = Value(6),
      chan7_raw = Value(7),
      chan8_raw = Value(8),
      chan9_raw = Value(9),
      chan10_raw = Value(10),
      chan11_raw = Value(11),
      chan12_raw = Value(12),
      chan13_raw = Value(13),
      chan14_raw = Value(14),
      chan15_raw = Value(15),
      chan16_raw = Value(16),
      chan17_raw = Value(17),
      chan18_raw = Value(18),
    };
  }

  public static MAVLink.mavlink_manual_control_t BuildManualControl(
      byte sysid, JoystickOutputSnapshot snapshot) {
    Validate(snapshot);
    short Value(int channel) => snapshot.Mapped[channel - 1]
        ? snapshot.Values[channel - 1]
        : (short)0;

    return new MAVLink.mavlink_manual_control_t {
      target = sysid,
      x = Value(1),
      y = Value(2),
      z = Value(3),
      r = Value(4),
    };
  }

  public static byte[] BuildSitlDatagram(JoystickOutputSnapshot snapshot) {
    Validate(snapshot);
    var packet = new byte[sizeof(ushort) * 8];
    for (int i = 0; i < 8; i++) {
      BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(i * sizeof(ushort), sizeof(ushort)),
          unchecked((ushort)snapshot.Values[i]));
    }
    return packet;
  }

  public static void ResetOverrides(CurrentState state) {
    for (int channel = 1; channel <= _channelCount; channel++) {
      SetOverride(state, channel, 0);
    }
  }

  private static void Validate(JoystickOutputSnapshot snapshot) {
    if (snapshot.Mapped == null || snapshot.Mapped.Length < _channelCount ||
        snapshot.Values == null || snapshot.Values.Length < _channelCount) {
      throw new ArgumentException("A joystick snapshot must contain all 18 channels.");
    }
  }

  private static short GetOverride(CurrentState state, int channel) => channel switch {
    1 => state.rcoverridech1,
    2 => state.rcoverridech2,
    3 => state.rcoverridech3,
    4 => state.rcoverridech4,
    5 => state.rcoverridech5,
    6 => state.rcoverridech6,
    7 => state.rcoverridech7,
    8 => state.rcoverridech8,
    9 => state.rcoverridech9,
    10 => state.rcoverridech10,
    11 => state.rcoverridech11,
    12 => state.rcoverridech12,
    13 => state.rcoverridech13,
    14 => state.rcoverridech14,
    15 => state.rcoverridech15,
    16 => state.rcoverridech16,
    17 => state.rcoverridech17,
    18 => state.rcoverridech18,
    _ => 0,
  };

  private static void SetOverride(CurrentState state, int channel, short value) {
    switch (channel) {
      case 1: state.rcoverridech1 = value; break;
      case 2: state.rcoverridech2 = value; break;
      case 3: state.rcoverridech3 = value; break;
      case 4: state.rcoverridech4 = value; break;
      case 5: state.rcoverridech5 = value; break;
      case 6: state.rcoverridech6 = value; break;
      case 7: state.rcoverridech7 = value; break;
      case 8: state.rcoverridech8 = value; break;
      case 9: state.rcoverridech9 = value; break;
      case 10: state.rcoverridech10 = value; break;
      case 11: state.rcoverridech11 = value; break;
      case 12: state.rcoverridech12 = value; break;
      case 13: state.rcoverridech13 = value; break;
      case 14: state.rcoverridech14 = value; break;
      case 15: state.rcoverridech15 = value; break;
      case 16: state.rcoverridech16 = value; break;
      case 17: state.rcoverridech17 = value; break;
      case 18: state.rcoverridech18 = value; break;
    }
  }
}

/// <summary>
/// Owns the active joystick and the upstream-compatible 20 Hz control sender.
/// Input acquisition remains platform-specific; outgoing control is shared by all platforms.
/// </summary>
internal sealed class JoystickControlService : IDisposable {
  private static readonly TimeSpan _sendInterval = TimeSpan.FromMilliseconds(50);

  private readonly object _sync = new();
  private readonly object _transition = new();
  private readonly MAVLinkInterface _comPort;
  private JoystickBase? _active;
  private CancellationTokenSource? _sendCts;
  private Task? _sendTask;
  private string? _lastSendError;
  private bool _disposed;

  public JoystickControlService(MAVLinkInterface comPort) {
    _comPort = comPort;
  }

  public event Action<JoystickBase, string>? Stopped;
  public event Action<string>? SendError;

  public JoystickBase? Active {
    get {
      lock (_sync) {
        return _active;
      }
    }
  }

  public void Start(JoystickBase joystick) {
    ArgumentNullException.ThrowIfNull(joystick);

    lock (_transition) {
      StartCore(joystick);
    }
  }

  private void StartCore(JoystickBase joystick) {

    JoystickBase? previous;
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      previous = _active;
    }
    if (previous != null && !ReferenceEquals(previous, joystick)) {
      Stop(previous, "Joystick replaced by another device.");
    }

    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (ReferenceEquals(_active, joystick)) {
        return;
      }

      var cts = new CancellationTokenSource();
      _active = joystick;
      _sendCts = cts;
      _lastSendError = null;
      _sendTask = Task.Run(() => SendLoop(joystick, cts));
    }
  }

  public bool Stop(JoystickBase joystick, string reason, bool clearOverride = true) {
    lock (_transition) {
      return StopCore(joystick, reason, clearOverride);
    }
  }

  private bool StopCore(JoystickBase joystick, string reason, bool clearOverride) {
    CancellationTokenSource? cts;
    Task? sendTask;
    lock (_sync) {
      if (!ReferenceEquals(_active, joystick)) {
        return false;
      }
      _active = null;
      cts = _sendCts;
      sendTask = _sendTask;
      _sendCts = null;
      _sendTask = null;
    }

    joystick.enabled = false;
    try {
      cts?.Cancel();
    } catch (ObjectDisposedException) {
    }

    // Do not block the Avalonia UI while an in-flight transport write unwinds. Cleanup runs after
    // the sender is definitely finished, so a stale frame cannot follow the release packet.
    if (sendTask is { IsCompleted: false }) {
      _ = FinishStopAfterSenderAsync(sendTask, joystick, reason, clearOverride);
    } else {
      FinishStoppedJoystick(joystick, reason, clearOverride);
    }
    return true;
  }

  private async Task FinishStopAfterSenderAsync(Task sendTask, JoystickBase joystick,
      string reason, bool clearOverride) {
    try {
      await sendTask.ConfigureAwait(false);
    } catch {
      // Sender failures are already deduplicated and reported through SendError.
    }
    FinishStoppedJoystick(joystick, reason, clearOverride);
  }

  private void FinishStoppedJoystick(JoystickBase joystick, string reason, bool clearOverride) {
    JoystickBase? active;
    lock (_sync) {
      active = _active;
    }
    if (ReferenceEquals(active, joystick)) {
      // The same instance was explicitly restarted before its old sender finished unwinding.
      return;
    }
    if (active == null) {
      ReleaseOverrides(joystick, clearOverride);
    }
    DisposeJoystick(joystick);
    Stopped?.Invoke(joystick, reason);
  }

  private bool StopFromSender(JoystickBase joystick, string reason) {
    lock (_transition) {
      return StopFromSenderCore(joystick, reason);
    }
  }

  private bool StopFromSenderCore(JoystickBase joystick, string reason) {
    CancellationTokenSource? cts;
    lock (_sync) {
      if (!ReferenceEquals(_active, joystick)) {
        return false;
      }
      _active = null;
      cts = _sendCts;
      _sendCts = null;
      _sendTask = null;
    }

    try {
      cts?.Cancel();
    } catch (ObjectDisposedException) {
    }

    ReleaseOverrides(joystick, clearOverride: true);
    DisposeJoystick(joystick);
    Stopped?.Invoke(joystick, reason);
    return true;
  }

  private void ReleaseOverrides(JoystickBase joystick, bool clearOverride) {
    // Built-in SITL receives raw RC on UDP 5501 rather than MAVLink RC override. Send an all-zero
    // frame too, which is the upstream SITL release convention, before clearing MAVLink overrides.
    var empty = new JoystickOutputSnapshot(false, new bool[18], new short[18]);
    SitlLauncher.TrySendRcInput(empty);

    if (clearOverride) {
      try {
        joystick.clearRCOverride();
      } catch {
        JoystickControlPackets.ResetOverrides(_comPort.MAV.cs);
      }
    } else {
      JoystickControlPackets.ResetOverrides(_comPort.MAV.cs);
    }
  }

  private static void DisposeJoystick(JoystickBase joystick) {
    try {
      joystick.UnAcquireJoyStick();
    } catch {
    }
    try {
      joystick.Dispose();
    } catch {
    }
  }

  public void StopActive(string reason, bool clearOverride = true) {
    var joystick = Active;
    if (joystick != null) {
      Stop(joystick, reason, clearOverride);
    }
  }

  public void HandleConnectionChanged(bool connected) {
    if (!connected) {
      StopActive("Vehicle connection closed. Reconnect and enable the joystick again.");
    }
  }

  public void Dispose() {
    lock (_sync) {
      if (_disposed) {
        return;
      }
      _disposed = true;
    }
    StopActive("Joystick service stopped.");
  }

  private async Task SendLoop(JoystickBase joystick, CancellationTokenSource cts) {
    try {
      while (!cts.IsCancellationRequested) {
        // JoystickBase starts its platform reader/normalizer independently. Give it one sender
        // interval to publish the first channel values instead of emitting an all-zero SITL frame.
        await Task.Delay(_sendInterval, cts.Token).ConfigureAwait(false);

        if (!joystick.enabled || !joystick.IsJoystickValid()) {
          StopFromSender(joystick, "Joystick disconnected. Reconnect it and enable it again.");
          return;
        }

        TrySendOnce(joystick);
      }
    } catch (OperationCanceledException) when (cts.IsCancellationRequested) {
    } finally {
      cts.Dispose();
    }
  }

  private void TrySendOnce(JoystickBase joystick) {
    try {
      if (_comPort.BaseStream?.IsOpen != true || _comPort.BaseStream.BytesToWrite >= 50) {
        return;
      }

      var mav = _comPort.MAV;
      var snapshot = JoystickControlPackets.Capture(joystick, mav.cs);
      if (!snapshot.ManualControl && SitlLauncher.TrySendRcInput(snapshot)) {
        _lastSendError = null;
        return;
      }

      if (snapshot.ManualControl) {
        var manual = JoystickControlPackets.BuildManualControl(mav.sysid, snapshot);
        _comPort.sendPacket(manual, mav.sysid, mav.compid);
      } else {
        var rc = JoystickControlPackets.BuildRcOverride(mav.sysid, mav.compid, snapshot);
        _comPort.sendPacket(rc, mav.sysid, mav.compid);
      }
      _lastSendError = null;
    } catch (Exception ex) {
      if (!string.Equals(_lastSendError, ex.Message, StringComparison.Ordinal)) {
        _lastSendError = ex.Message;
        SendError?.Invoke("Joystick control send failed: " + ex.Message);
      }
    }
  }
}
