using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using MissionPlanner;

namespace MissionPlannerAvalonia.Services;

internal static class NvModemMessageIds {
  internal const uint NvRxStat = 53002;
  internal const uint Nv5LinkStatus = 53010;
  internal const uint Nv5RtspConfig = 53014;
  internal const uint Nv5RtspConfigAck = 53015;
  internal const uint NvModemInfo = 53016;
}

internal static class NvModemInfoFlags {
  internal const byte Channel1Active = 1 << 0;
  internal const byte Channel2Active = 1 << 1;
}

internal static class NvModemCapabilities {
  internal const ulong Rtsp = 1UL << 8;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 28)]
internal struct NvRxStatMessage {
  internal int BootTime;
  internal float Snr;
  internal float Quality;
  internal uint BytesReceived;
  internal uint MavParsed;
  internal uint Frequency;
  internal uint Bandwidth;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 78)]
internal struct Nv5LinkStatusMessage {
  internal uint TimeBootMs;
  internal uint SampleMs;
  internal uint FrequencyHz;
  internal uint BandwidthHz;
  internal uint TxApplicationBytes;
  internal uint RxApplicationBytes;
  internal uint TxRadioBytes;
  internal uint RxRadioBytes;
  internal uint TxFrames;
  internal uint RxFrames;
  internal uint Errors;
  internal uint DroppedBytes;
  internal uint FecRecovered;
  internal uint HopMissed;
  internal uint SyncLost;
  internal ushort TxQueueBytes;
  internal ushort RxQueueBytes;
  internal short PacketRssiDbmX10;
  internal short PacketSnrDbX10;
  internal short ChannelRssiDbmX10;
  internal byte Channel;
  internal byte RadioChip;
  internal byte Role;
  internal byte Modulation;
  internal byte Flags;
  internal byte Txbuf;
  internal byte LinkQuality;
  internal byte TxState;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 103)]
internal struct Nv5RtspConfigMessage {
  internal uint TransactionId;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte Operation;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 96)]
  internal byte[] Path;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 9)]
internal struct Nv5RtspConfigAckMessage {
  internal uint TransactionId;
  internal ushort Detail;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte Result;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 53)]
internal struct NvModemInfoMessage {
  internal ulong Capabilities;
  internal uint TimeBootMs;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
  internal byte[] BuildHash;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 18)]
  internal byte[] Uid2;

  internal byte SchemaVersion;
  internal byte ModemGeneration;
  internal byte HardwareVersionMajor;
  internal byte HardwareVersionMinor;
  internal byte FirmwareVersionMajor;
  internal byte FirmwareVersionMinor;
  internal byte FirmwareVersionPatch;
  internal byte ProtocolVersion;
  internal byte ProductProfile;
  internal byte RadioCount;
  internal byte Flags;
  internal byte Channel1Role;
  internal byte Channel2Role;
  internal byte Channel1RadioChip;
  internal byte Channel2RadioChip;
}

/// <summary>
/// Registers the SkyComm messages used by NV5Settings with Mission Planner's generated MAVLink
/// parser. The upstream MAVLink table is public and mutable, which lets the port add a private
/// dialect without editing the pinned Mission Planner submodule.
/// </summary>
internal static class NvModemMavlinkDialect {
  private static readonly object Sync = new();

  internal static void Register() {
    lock (Sync) {
      var existing = MAVLink.MAVLINK_MESSAGE_INFOS;
      var additions = MessageInfos()
          .Where(candidate => existing.All(info => info.msgid != candidate.msgid))
          .ToArray();
      if (additions.Length != 0) {
        MAVLink.MAVLINK_MESSAGE_INFOS = [.. existing, .. additions];
      }
    }
  }

  private static MAVLink.message_info[] MessageInfos() => [
    new(NvModemMessageIds.NvRxStat, "NV_RX_STAT", 49, 28, 28,
        typeof(NvRxStatMessage)),
    new(NvModemMessageIds.Nv5LinkStatus, "NV5_LINK_STATUS", 165, 77, 78,
        typeof(Nv5LinkStatusMessage)),
    new(NvModemMessageIds.Nv5RtspConfig, "NV5_RTSP_CONFIG", 127, 103, 103,
        typeof(Nv5RtspConfigMessage)),
    new(NvModemMessageIds.Nv5RtspConfigAck, "NV5_RTSP_CONFIG_ACK", 193, 9, 9,
        typeof(Nv5RtspConfigAckMessage)),
    new(NvModemMessageIds.NvModemInfo, "NV_MODEM_INFO", 207, 53, 53,
        typeof(NvModemInfoMessage)),
  ];
}

internal static class NvModemParameterCodec {
  internal static string Name(byte[]? bytes) {
    if (bytes == null) {
      return "";
    }
    int length = Array.IndexOf(bytes, (byte)0);
    if (length < 0) {
      length = bytes.Length;
    }
    return Encoding.ASCII.GetString(bytes, 0, length);
  }

  internal static byte[] NameBytes(string name) {
    byte[] target = new byte[16];
    byte[] source = Encoding.ASCII.GetBytes(name);
    Array.Copy(source, target, Math.Min(source.Length, target.Length));
    return target;
  }

  internal static double Decode(float wireValue, byte type) {
    int bits = BitConverter.SingleToInt32Bits(wireValue);
    return (MAVLink.MAV_PARAM_TYPE)type switch {
      MAVLink.MAV_PARAM_TYPE.UINT8 => (double)(byte)bits,
      MAVLink.MAV_PARAM_TYPE.INT8 => (double)(sbyte)bits,
      MAVLink.MAV_PARAM_TYPE.UINT16 => (double)(ushort)bits,
      MAVLink.MAV_PARAM_TYPE.INT16 => (double)(short)bits,
      MAVLink.MAV_PARAM_TYPE.UINT32 => unchecked((double)(uint)bits),
      MAVLink.MAV_PARAM_TYPE.INT32 => (double)bits,
      _ => (double)wireValue,
    };
  }

  internal static float Encode(double value, byte type) {
    int bits = (MAVLink.MAV_PARAM_TYPE)type switch {
      MAVLink.MAV_PARAM_TYPE.UINT8 => checked((byte)Math.Round(value)),
      MAVLink.MAV_PARAM_TYPE.INT8 => checked((sbyte)Math.Round(value)),
      MAVLink.MAV_PARAM_TYPE.UINT16 => checked((ushort)Math.Round(value)),
      MAVLink.MAV_PARAM_TYPE.INT16 => checked((short)Math.Round(value)),
      MAVLink.MAV_PARAM_TYPE.UINT32 => unchecked((int)checked((uint)Math.Round(value))),
      MAVLink.MAV_PARAM_TYPE.INT32 => checked((int)Math.Round(value)),
      _ => 0,
    };
    return IsInteger(type) ? BitConverter.Int32BitsToSingle(bits) : (float)value;
  }

  internal static bool IsInteger(byte type) =>
      type >= (byte)MAVLink.MAV_PARAM_TYPE.UINT8
      && type <= (byte)MAVLink.MAV_PARAM_TYPE.INT32;

  internal static string Display(double value, byte type) => IsInteger(type)
      ? Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
      : value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);

  internal static bool TryParse(string text, byte type, out double value) {
    bool parsed = double.TryParse(text,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out value);
    return parsed && IsValid(value, type);
  }

  internal static bool IsValid(double value, byte type) {
    if (!double.IsFinite(value)) {
      return false;
    }
    if (!IsInteger(type)) {
      return true;
    }
    if (value != Math.Floor(value)) {
      return false;
    }
    return (MAVLink.MAV_PARAM_TYPE)type switch {
      MAVLink.MAV_PARAM_TYPE.UINT8 => value is >= byte.MinValue and <= byte.MaxValue,
      MAVLink.MAV_PARAM_TYPE.INT8 => value is >= sbyte.MinValue and <= sbyte.MaxValue,
      MAVLink.MAV_PARAM_TYPE.UINT16 => value is >= ushort.MinValue and <= ushort.MaxValue,
      MAVLink.MAV_PARAM_TYPE.INT16 => value is >= short.MinValue and <= short.MaxValue,
      MAVLink.MAV_PARAM_TYPE.UINT32 => value is >= uint.MinValue and <= uint.MaxValue,
      MAVLink.MAV_PARAM_TYPE.INT32 => value is >= int.MinValue and <= int.MaxValue,
      _ => true,
    };
  }

  internal static bool NearlyEqual(double left, double right) {
    double scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
    return Math.Abs(left - right) <= scale * 1e-7;
  }
}

internal sealed record NvModemLink(MAVLinkInterface Link, string Name);

internal readonly record struct NvModemEndpoint(byte SystemId, byte ComponentId);

internal interface INvModemMavlinkTransport : IDisposable {
  event Action<NvModemLink, MAVLink.MAVLinkMessage>? PacketReceived;
  event Action? LinksChanged;

  IReadOnlyList<NvModemLink> Snapshot();
  IReadOnlyList<NvModemEndpoint> KnownEndpoints(NvModemLink source);
  IReadOnlyList<MAVLink.MAVLinkMessage> CachedDiscoveryPackets(NvModemLink source);
  bool Send(NvModemLink source, object packet, byte systemId, byte componentId);
}

internal sealed class NvModemMavlinkTransport : INvModemMavlinkTransport {
  private readonly object _sync = new();
  private readonly Dictionary<MAVLinkInterface, NvModemLink> _links =
      new(ReferenceEqualityComparer.Instance);
  private bool _disposed;

  internal NvModemMavlinkTransport() {
    NvModemMavlinkDialect.Register();
    AppState.Connections.Changed += OnConnectionsChanged;
    AppState.ConnectionChanged += OnConnectionsChanged;
    RefreshLinks();
  }

  public event Action<NvModemLink, MAVLink.MAVLinkMessage>? PacketReceived;
  public event Action? LinksChanged;

  public IReadOnlyList<NvModemLink> Snapshot() {
    // Opening the already-active primary connection does not necessarily change the connection
    // manager's selection. Refresh here so the Discover button cannot miss that shared link.
    RefreshLinks();
    lock (_sync) {
      return _links.Values.ToArray();
    }
  }

  public IReadOnlyList<NvModemEndpoint> KnownEndpoints(NvModemLink source) {
    try {
      return source.Link.MAVlist.ToArray()
          .Where(mav => mav.lastvalidpacket > DateTime.MinValue
              && (mav.sysid != 0 || mav.compid != 0))
          .Select(mav => new NvModemEndpoint(mav.sysid, mav.compid))
          .Distinct()
          .ToArray();
    } catch {
      return [];
    }
  }

  public IReadOnlyList<MAVLink.MAVLinkMessage> CachedDiscoveryPackets(NvModemLink source) {
    try {
      uint[] messageIds = [
        NvModemMessageIds.NvModemInfo,
        NvModemMessageIds.Nv5LinkStatus,
        NvModemMessageIds.Nv5RtspConfig,
        NvModemMessageIds.Nv5RtspConfigAck,
        NvModemMessageIds.NvRxStat,
        (uint)MAVLink.MAVLINK_MSG_ID.UAVCAN_NODE_INFO,
        (uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE,
      ];
      var packets = new List<MAVLink.MAVLinkMessage>();
      foreach (MAVState mav in source.Link.MAVlist.ToArray()) {
        foreach (uint messageId in messageIds) {
          MAVLink.MAVLinkMessage? packet = mav.getPacketLast(messageId);
          if (packet != null) {
            packets.Add(packet);
          }
        }
      }
      return packets;
    } catch {
      return [];
    }
  }

  public bool Send(NvModemLink source, object packet, byte systemId, byte componentId) {
    try {
      if (source.Link.BaseStream?.IsOpen != true) {
        return false;
      }
      source.Link.sendPacket(packet, systemId, componentId);
      return true;
    } catch {
      return false;
    }
  }

  private void OnConnectionsChanged() {
    RefreshLinks();
    LinksChanged?.Invoke();
  }

  private void RefreshLinks() {
    MissionPlannerAvalonia.Services.MavLinkConnection[] connections =
        [.. AppState.Connections.Snapshot().Where(connection => connection.IsOpen)];
    lock (_sync) {
      if (_disposed) {
        return;
      }
      foreach (MAVLinkInterface removed in _links.Keys
                   .Where(link => connections.All(item => !ReferenceEquals(item.Link, link)))
                   .ToArray()) {
        removed.OnPacketReceived -= OnPacketReceived;
        _links.Remove(removed);
      }
      foreach (var connection in connections) {
        if (_links.ContainsKey(connection.Link)) {
          continue;
        }
        var source = new NvModemLink(connection.Link, connection.Endpoint);
        _links.Add(connection.Link, source);
        connection.Link.OnPacketReceived += OnPacketReceived;
      }
    }
  }

  private void OnPacketReceived(object? sender, MAVLink.MAVLinkMessage packet) {
    NvModemLink? source;
    lock (_sync) {
      source = sender is MAVLinkInterface link && _links.TryGetValue(link, out var found)
          ? found : null;
    }
    if (source != null) {
      PacketReceived?.Invoke(source, packet);
    }
  }

  public void Dispose() {
    lock (_sync) {
      if (_disposed) {
        return;
      }
      _disposed = true;
      AppState.Connections.Changed -= OnConnectionsChanged;
      AppState.ConnectionChanged -= OnConnectionsChanged;
      foreach (MAVLinkInterface link in _links.Keys) {
        link.OnPacketReceived -= OnPacketReceived;
      }
      _links.Clear();
    }
  }
}
