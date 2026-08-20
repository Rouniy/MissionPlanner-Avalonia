using System;
using MissionPlanner;

namespace MissionPlannerAvalonia.Services;

internal sealed class Px4FlowFrameEventArgs : EventArgs {
  public Px4FlowFrameEventArgs(int width, int height, byte[] grayscale) {
    Width = width;
    Height = height;
    Grayscale = grayscale;
  }

  public int Width { get; }
  public int Height { get; }
  public byte[] Grayscale { get; }
}

/// <summary>
/// Receives PX4Flow grayscale frames without the upstream System.Drawing.Bitmap path,
/// which is Windows-only on current .NET runtimes.
/// </summary>
internal sealed class Px4FlowReceiver : IDisposable {
  private const int _maxFrameBytes = 16 * 1024 * 1024;

  private readonly object _sync = new();
  private readonly MAVLinkInterface _mav;
  private readonly byte _sysid;
  private readonly byte _compid;
  private readonly int _handshakeSubscription;
  private readonly int _dataSubscription;

  private byte[]? _frame;
  private bool[]? _receivedPackets;
  private int _receivedCount;
  private int _width;
  private int _height;
  private int _payloadSize;
  private int _packetCount;
  private bool _complete;
  private bool _disposed;

  public Px4FlowReceiver(MAVLinkInterface mav, byte sysid, byte compid) {
    _mav = mav;
    _sysid = sysid;
    _compid = compid;
    _handshakeSubscription = mav.SubscribeToPacketType(
        MAVLink.MAVLINK_MSG_ID.DATA_TRANSMISSION_HANDSHAKE, ReceivePacket, sysid, compid);
    _dataSubscription = mav.SubscribeToPacketType(
        MAVLink.MAVLINK_MSG_ID.ENCAPSULATED_DATA, ReceivePacket, sysid, compid);
  }

  public event EventHandler<Px4FlowFrameEventArgs>? FrameReceived;

  public void CalibrationMode(bool enabled) =>
      _mav.setParam(_sysid, _compid, "VIDEO_ONLY", enabled ? 1 : 0);

  private bool ReceivePacket(MAVLink.MAVLinkMessage message) {
    Px4FlowFrameEventArgs? completedFrame = null;
    lock (_sync) {
      if (_disposed) {
        return true;
      }

      if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.DATA_TRANSMISSION_HANDSHAKE) {
        StartFrame(message.ToStructure<MAVLink.mavlink_data_transmission_handshake_t>());
      } else if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.ENCAPSULATED_DATA) {
        completedFrame = AddPacket(message.ToStructure<MAVLink.mavlink_encapsulated_data_t>());
      }
    }

    if (completedFrame != null) {
      FrameReceived?.Invoke(this, completedFrame);
    }
    return true;
  }

  private void StartFrame(MAVLink.mavlink_data_transmission_handshake_t handshake) {
    int size = checked((int)handshake.size);
    int packets = handshake.packets;
    int payload = handshake.payload;
    int width = handshake.width;
    int height = handshake.height;
    if (size <= 0 || size > _maxFrameBytes || packets <= 0 || payload <= 0 ||
        width <= 0 || height <= 0 || (long)width * height > _maxFrameBytes) {
      ResetFrame();
      return;
    }

    _frame = new byte[size];
    _receivedPackets = new bool[packets];
    _receivedCount = 0;
    _width = width;
    _height = height;
    _payloadSize = payload;
    _packetCount = packets;
    _complete = false;
  }

  private Px4FlowFrameEventArgs? AddPacket(MAVLink.mavlink_encapsulated_data_t packet) {
    if (_frame == null || _receivedPackets == null || _complete) {
      return null;
    }

    int sequence = packet.seqnr;
    if (sequence < 0 || sequence >= _packetCount) {
      return null;
    }

    long startLong = (long)_payloadSize * sequence;
    if (startLong < 0 || startLong >= _frame.Length) {
      return null;
    }

    int start = (int)startLong;
    int count = Math.Min(packet.data.Length, _frame.Length - start);
    Array.Copy(packet.data, 0, _frame, start, count);
    if (!_receivedPackets[sequence]) {
      _receivedPackets[sequence] = true;
      _receivedCount++;
    }

    if (_receivedCount != _packetCount) {
      return null;
    }

    _complete = true;
    return new Px4FlowFrameEventArgs(_width, _height, (byte[])_frame.Clone());
  }

  private void ResetFrame() {
    _frame = null;
    _receivedPackets = null;
    _receivedCount = 0;
    _width = 0;
    _height = 0;
    _payloadSize = 0;
    _packetCount = 0;
    _complete = false;
  }

  internal static byte[] ToBgra(byte[] grayscale, int width, int height) {
    if (width <= 0 || height <= 0) {
      return Array.Empty<byte>();
    }

    int pixels = checked(width * height);
    var bgra = new byte[checked(pixels * 4)];
    int count = Math.Min(grayscale.Length, pixels);
    for (int i = 0; i < pixels; i++) {
      byte value = i < count ? grayscale[i] : (byte)0;
      int target = i * 4;
      bgra[target] = value;
      bgra[target + 1] = value;
      bgra[target + 2] = value;
      bgra[target + 3] = byte.MaxValue;
    }
    return bgra;
  }

  public void Dispose() {
    lock (_sync) {
      if (_disposed) {
        return;
      }
      _disposed = true;
      ResetFrame();
    }
    _mav.UnSubscribeToPacketType(_handshakeSubscription);
    _mav.UnSubscribeToPacketType(_dataSubscription);
  }
}
