using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Comms;

namespace MissionPlannerAvalonia.ViewModels.GCSViews.ConfigurationView;

public sealed record DroneCanNetworkInterfaceOption(
    string Id,
    string Name,
    string Description,
    int IPv4Index) {
  public string DisplayName => string.Equals(Name, Description, StringComparison.Ordinal)
      ? $"{Name} (IPv4 index {IPv4Index})"
      : $"{Description} — {Name} (IPv4 index {IPv4Index})";

  public override string ToString() => DisplayName;
}

internal readonly record struct DroneCanMulticastFrame(
    uint Identifier,
    bool Extended,
    bool CanFd,
    byte[] Payload);

internal static class DroneCanMulticastCodec {
  internal const ushort Magic = 0x2934;
  internal const ushort CanFdFlag = 0x0001;
  internal const int Port = 57732;
  internal const int HeaderLength = 10;
  internal const int MaximumPayloadLength = 64;
  internal const int MaximumPacketLength = HeaderLength + MaximumPayloadLength;

  internal static IPAddress GroupForBus(byte bus) {
    if (bus > 1) {
      throw new ArgumentOutOfRangeException(nameof(bus), "Multicast CAN bus must be 0 or 1.");
    }
    return IPAddress.Parse($"239.65.82.{bus}");
  }

  internal static byte[] Encode(DroneCanMulticastFrame frame) {
    if (frame.Payload == null) {
      throw new ArgumentNullException(nameof(frame), "The multicast CAN payload is required.");
    }
    if (frame.Payload.Length > MaximumPayloadLength) {
      throw new ArgumentOutOfRangeException(nameof(frame), "A CAN-FD payload cannot exceed 64 bytes.");
    }
    if (!frame.CanFd && frame.Payload.Length > 8) {
      throw new ArgumentOutOfRangeException(nameof(frame), "A classic CAN payload cannot exceed 8 bytes.");
    }

    uint maximumIdentifier = frame.Extended ? 0x1FFFFFFFu : 0x7FFu;
    if (frame.Identifier > maximumIdentifier) {
      throw new ArgumentOutOfRangeException(nameof(frame), "The CAN identifier exceeds its wire format.");
    }

    byte[] packet = new byte[HeaderLength + frame.Payload.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), Magic);
    BinaryPrimitives.WriteUInt16LittleEndian(
        packet.AsSpan(4, 2), frame.CanFd ? CanFdFlag : (ushort)0);
    uint wireIdentifier = frame.Identifier | (frame.Extended ? 0x80000000u : 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6, 4), wireIdentifier);
    frame.Payload.CopyTo(packet, HeaderLength);

    byte[] crcBody = packet.AsSpan(4).ToArray();
    ushort crc = global::DroneCAN.TransferCRC.compute(crcBody, crcBody.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), crc);
    return packet;
  }

  internal static bool TryDecode(ReadOnlySpan<byte> packet, out DroneCanMulticastFrame frame) {
    frame = default;
    if (packet.Length < HeaderLength || packet.Length > MaximumPacketLength ||
        BinaryPrimitives.ReadUInt16LittleEndian(packet[..2]) != Magic) {
      return false;
    }

    ushort expectedCrc = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2, 2));
    byte[] crcBody = packet[4..].ToArray();
    if (expectedCrc != global::DroneCAN.TransferCRC.compute(crcBody, crcBody.Length)) {
      return false;
    }

    ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(4, 2));
    if ((flags & ~CanFdFlag) != 0) {
      return false;
    }
    uint wireIdentifier = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(6, 4));
    if ((wireIdentifier & 0x60000000u) != 0) {
      return false;
    }
    bool extended = (wireIdentifier & 0x80000000u) != 0;
    uint identifier = wireIdentifier & 0x1FFFFFFFu;
    bool canFd = (flags & CanFdFlag) != 0;
    int payloadLength = packet.Length - HeaderLength;
    if ((!extended && identifier > 0x7FFu) || (!canFd && payloadLength > 8)) {
      return false;
    }

    frame = new DroneCanMulticastFrame(
        identifier, extended, canFd, packet[HeaderLength..].ToArray());
    return true;
  }

  internal static bool TryEncodeSlcan(ReadOnlySpan<byte> line, out byte[] packet) {
    packet = [];
    if (line.Length < 5) {
      return false;
    }

    char prefix = (char)line[0];
    bool extended;
    bool canFd;
    int identifierDigits;
    switch (prefix) {
      case 'T':
        extended = true;
        canFd = false;
        identifierDigits = 8;
        break;
      case 't':
        extended = false;
        canFd = false;
        identifierDigits = 3;
        break;
      case 'B':
      case 'D':
        extended = true;
        canFd = true;
        identifierDigits = 8;
        break;
      case 'b':
      case 'd':
        extended = false;
        canFd = true;
        identifierDigits = 3;
        break;
      default:
        return false;
    }

    if (line.Length < 2 + identifierDigits ||
        !TryParseHex(line.Slice(1, identifierDigits), out uint identifier) ||
        !TryHexNibble(line[1 + identifierDigits], out int dlc)) {
      return false;
    }

    int payloadLength = DroneCAN.DroneCAN.dlcToDataLength((byte)dlc);
    int payloadOffset = 2 + identifierDigits;
    if (payloadLength > MaximumPayloadLength || line.Length < payloadOffset + payloadLength * 2) {
      return false;
    }

    byte[] payload = new byte[payloadLength];
    for (int i = 0; i < payload.Length; i++) {
      if (!TryHexNibble(line[payloadOffset + i * 2], out int high) ||
          !TryHexNibble(line[payloadOffset + i * 2 + 1], out int low)) {
        return false;
      }
      payload[i] = (byte)((high << 4) | low);
    }

    try {
      packet = Encode(new DroneCanMulticastFrame(identifier, extended, canFd, payload));
      return true;
    } catch (ArgumentOutOfRangeException) {
      return false;
    }
  }

  internal static byte[] ToSlcan(DroneCanMulticastFrame frame) {
    byte dlc = DroneCAN.DroneCAN.dataLengthToDlc(frame.Payload.Length);
    char prefix = frame.CanFd
        ? (frame.Extended ? 'D' : 'd')
        : (frame.Extended ? 'T' : 't');
    int identifierDigits = frame.Extended ? 8 : 3;
    string identifier = frame.Identifier.ToString($"X{identifierDigits}");
    int encodedPayloadLength = DroneCAN.DroneCAN.dlcToDataLength(dlc);
    string payload = Convert.ToHexString(frame.Payload).PadRight(encodedPayloadLength * 2, '0');
    return Encoding.ASCII.GetBytes(
        $"{prefix}{identifier}{dlc:X}{payload}\r");
  }

  private static bool TryParseHex(ReadOnlySpan<byte> text, out uint value) {
    value = 0;
    foreach (byte character in text) {
      if (!TryHexNibble(character, out int nibble)) {
        value = 0;
        return false;
      }
      value = (value << 4) | (uint)nibble;
    }
    return true;
  }

  private static bool TryHexNibble(byte character, out int value) {
    if (character is >= (byte)'0' and <= (byte)'9') {
      value = character - '0';
      return true;
    }
    if (character is >= (byte)'A' and <= (byte)'F') {
      value = character - 'A' + 10;
      return true;
    }
    if (character is >= (byte)'a' and <= (byte)'f') {
      value = character - 'a' + 10;
      return true;
    }
    value = 0;
    return false;
  }
}

internal interface IDroneCanMulticastSession : IDisposable {
  ICommsSerial Serial { get; }
  string Endpoint { get; }
  event Action<Exception>? TransportFailed;
  void Start();
  void Stop();
}

internal sealed class DroneCanMulticastSession : IDroneCanMulticastSession {
  private readonly DroneCanNetworkInterfaceOption _networkInterface;
  private readonly byte _bus;
  private readonly CommsInjection _serial = new();
  private readonly object _writeLock = new();
  private readonly List<byte> _pendingWrite = [];
  private UdpClient? _udp;
  private IPEndPoint? _destination;
  private CancellationTokenSource? _cancellation;
  private Task? _receiveTask;
  private bool _started;
  private bool _disposed;

  internal DroneCanMulticastSession(DroneCanNetworkInterfaceOption networkInterface, byte bus) {
    _networkInterface = networkInterface;
    _bus = bus;
  }

  public ICommsSerial Serial => _serial;
  public string Endpoint => $"{DroneCanMulticastCodec.GroupForBus(_bus)}:{DroneCanMulticastCodec.Port}";
  public event Action<Exception>? TransportFailed;
  public void Start() {
    if (_disposed) {
      throw new ObjectDisposedException(nameof(DroneCanMulticastSession));
    }
    if (_started) {
      return;
    }

    IPAddress group = DroneCanMulticastCodec.GroupForBus(_bus);
    var udp = new UdpClient(AddressFamily.InterNetwork);
    try {
      udp.Client.ExclusiveAddressUse = false;
      udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
      udp.Client.SetSocketOption(
          SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
          IPAddress.HostToNetworkOrder(_networkInterface.IPv4Index));
      udp.Client.ReceiveTimeout = 250;
      udp.MulticastLoopback = true;
      udp.Ttl = 1;
      udp.Client.Bind(new IPEndPoint(IPAddress.Any, DroneCanMulticastCodec.Port));
      udp.JoinMulticastGroup(group, _networkInterface.IPv4Index);
    } catch {
      udp.Dispose();
      throw;
    }

    _udp = udp;
    _destination = new IPEndPoint(group, DroneCanMulticastCodec.Port);
    _cancellation = new CancellationTokenSource();
    _serial.WriteCallback += OnSerialWrite;
    _started = true;
    _receiveTask = Task.Run(() => ReceiveLoop(_cancellation.Token));
  }

  private void OnSerialWrite(object? sender, IEnumerable<byte> bytes) {
    lock (_writeLock) {
      foreach (byte value in bytes) {
        if (value is (byte)'\r' or (byte)'\n') {
          if (_pendingWrite.Count > 0) {
            HandleSerialLine(_pendingWrite.ToArray());
            _pendingWrite.Clear();
          }
        } else {
          _pendingWrite.Add(value);
          if (_pendingWrite.Count > 150) {
            _pendingWrite.Clear();
          }
        }
      }
    }
  }

  private void HandleSerialLine(byte[] line) {
    if (DroneCanMulticastCodec.TryEncodeSlcan(line, out byte[] packet)) {
      UdpClient? udp = _udp;
      IPEndPoint? destination = _destination;
      if (!_started || udp == null || destination == null) {
        return;
      }
      try {
        udp.Send(packet, packet.Length, destination);
      } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) {
        if (_started) {
          TransportFailed?.Invoke(ex);
        }
      }
      return;
    }

    // The virtual SLCAN link has no physical adapter to answer C/S/N/V/O/F. An empty success
    // response keeps the inherited DroneCAN startup handshake intact without delaying each step.
    _serial.AppendBuffer([(byte)'\r']);
  }

  private void ReceiveLoop(CancellationToken cancellationToken) {
    var source = new IPEndPoint(IPAddress.Any, 0);
    while (!cancellationToken.IsCancellationRequested) {
      try {
        byte[] packet = _udp!.Receive(ref source);
        if (DroneCanMulticastCodec.TryDecode(packet, out DroneCanMulticastFrame frame)) {
          _serial.AppendBuffer(DroneCanMulticastCodec.ToSlcan(frame));
        }
      } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) {
      } catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) {
        return;
      } catch (Exception ex) {
        if (!cancellationToken.IsCancellationRequested) {
          TransportFailed?.Invoke(ex);
        }
        return;
      }
    }
  }

  public void Stop() {
    if (!_started) {
      return;
    }
    _started = false;
    _serial.WriteCallback -= OnSerialWrite;
    CancellationTokenSource? cancellation = _cancellation;
    UdpClient? udp = _udp;
    _cancellation = null;
    _udp = null;
    _destination = null;
    cancellation?.Cancel();
    udp?.Dispose();
    try {
      _receiveTask?.Wait(TimeSpan.FromSeconds(1));
    } catch {
    }
    _receiveTask = null;
    cancellation?.Dispose();
    _serial.Close();
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    Stop();
    _serial.Dispose();
  }

  internal static IReadOnlyList<DroneCanNetworkInterfaceOption> GetAvailableInterfaces() {
    try {
      return NetworkInterface.GetAllNetworkInterfaces()
          .Where(item => item.SupportsMulticast &&
              item.OperationalStatus == OperationalStatus.Up)
          .Select(item => {
            IPv4InterfaceProperties? ipv4;
            try {
              ipv4 = item.GetIPProperties().GetIPv4Properties();
            } catch {
              ipv4 = null;
            }
            return ipv4 == null
                ? null
                : new DroneCanNetworkInterfaceOption(
                    item.Id, item.Name, item.Description, ipv4.Index);
          })
          .Where(item => item != null)
          .Cast<DroneCanNetworkInterfaceOption>()
          .OrderBy(item => item.Description, StringComparer.OrdinalIgnoreCase)
          .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
          .ToArray();
    } catch {
      return [];
    }
  }
}
