using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MissionPlannerAvalonia.Services;

public sealed record TlogMissionItem(
    ushort Sequence, byte Current, byte Frame, ushort Command,
    float Param1, float Param2, float Param3, float Param4,
    double X, double Y, float Z, byte AutoContinue);

public static class TlogExportService {
  public static int WriteCsv(string input, string output) =>
      WriteDecodedPackets(input, output, ",", csv: true);

  public static int WriteHumanReadable(string input, string output) =>
      WriteDecodedPackets(input, output, " ", csv: false);

  private static int WriteDecodedPackets(string input, string output, string delimiter, bool csv) {
    int count = 0;
    using var writer = new StreamWriter(output, false, new UTF8Encoding(false));
    using var mav = new MissionPlanner.MAVLinkInterface();
    foreach (var packet in ReadPackets(input)) {
      string text = "";
      mav.DebugPacket(packet, ref text, false, delimiter);
      if (string.IsNullOrWhiteSpace(text)) {
        continue;
      }
      string timestamp = packet.rxtime.ToUniversalTime().ToString(
          csv ? "yyyy-MM-dd'T'HH:mm:ss.fff'Z'" : "O", CultureInfo.InvariantCulture);
      writer.Write(timestamp);
      writer.Write(csv ? "," : " ");
      writer.Write(text);
      count++;
    }
    return count;
  }

  public static int WriteParameters(string input, string output) {
    var values = ExtractParameters(ReadPackets(input));
    using var writer = new StreamWriter(output, false, new UTF8Encoding(false));
    foreach (var item in values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)) {
      writer.Write(item.Key);
      writer.Write('\t');
      writer.WriteLine(item.Value.ToString("G17", CultureInfo.InvariantCulture));
    }
    return values.Count;
  }

  internal static IReadOnlyDictionary<string, double> ExtractParameters(
      IEnumerable<MAVLink.MAVLinkMessage> packets) {
    var ardupilotSystems = new HashSet<(byte System, byte Component)>();
    var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    foreach (var packet in packets) {
      if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT) {
        var heartbeat = packet.ToStructure<MAVLink.mavlink_heartbeat_t>();
        if (heartbeat.autopilot == (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA) {
          ardupilotSystems.Add((packet.sysid, packet.compid));
        }
        continue;
      }
      if (packet.msgid != (uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE) {
        continue;
      }

      var parameter = packet.ToStructure<MAVLink.mavlink_param_value_t>();
      string name = Encoding.UTF8.GetString(parameter.param_id).TrimEnd('\0');
      if (string.IsNullOrWhiteSpace(name)) {
        continue;
      }

      var declaredType = (MAVLink.MAV_PARAM_TYPE)parameter.param_type;
      var wireType = ardupilotSystems.Contains((packet.sysid, packet.compid))
          ? MAVLink.MAV_PARAM_TYPE.REAL32
          : declaredType;
      try {
        values[name] = new MAVLink.MAVLinkParam(
            name, BitConverter.GetBytes(parameter.param_value), wireType, declaredType).Value;
      } catch {
        values[name] = parameter.param_value;
      }
    }
    return values;
  }

  public static IReadOnlyList<string> WriteWaypointSnapshots(string input, string selectedOutput) {
    var snapshots = ExtractMissionSnapshots(ReadPackets(input));
    var outputs = new List<string>();
    for (int index = 0; index < snapshots.Count; index++) {
      string path = index == 0
          ? selectedOutput
          : Path.Combine(
              Path.GetDirectoryName(selectedOutput) ?? "",
              Path.GetFileNameWithoutExtension(selectedOutput) + $"-{index + 1}" +
              Path.GetExtension(selectedOutput));
      WriteQgcWpl(snapshots[index], path);
      outputs.Add(path);
    }
    return outputs;
  }

  internal static IReadOnlyList<IReadOnlyList<TlogMissionItem>> ExtractMissionSnapshots(
      IEnumerable<MAVLink.MAVLinkMessage> packets) {
    var builders = new Dictionary<(byte System, byte Component), MissionBuilder>();
    var snapshots = new List<IReadOnlyList<TlogMissionItem>>();
    var signatures = new HashSet<string>(StringComparer.Ordinal);

    foreach (var packet in packets) {
      var key = (packet.sysid, packet.compid);
      if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_COUNT) {
        var count = packet.ToStructure<MAVLink.mavlink_mission_count_t>();
        if (count.mission_type != (byte)MAVLink.MAV_MISSION_TYPE.MISSION || count.count == 0) {
          builders.Remove(key);
          continue;
        }
        if (!builders.TryGetValue(key, out var existing)
            || existing.Expected != count.count || existing.Items.Count == 0) {
          builders[key] = new MissionBuilder(count.count);
        }
        continue;
      }

      TlogMissionItem? item = packet.msgid switch {
        (uint)MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT => FromInt(
            packet.ToStructure<MAVLink.mavlink_mission_item_int_t>()),
        (uint)MAVLink.MAVLINK_MSG_ID.MISSION_ITEM => FromFloat(
            packet.ToStructure<MAVLink.mavlink_mission_item_t>()),
        _ => null,
      };
      if (item == null || !builders.TryGetValue(key, out var builder)
          || item.Sequence >= builder.Expected) {
        continue;
      }

      builder.Items[item.Sequence] = item;
      if (builder.Items.Count != builder.Expected
          || Enumerable.Range(0, builder.Expected).Any(seq => !builder.Items.ContainsKey((ushort)seq))) {
        continue;
      }

      var snapshot = builder.Items.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
      string signature = string.Join("|", snapshot.Select(MissionSignature));
      if (signatures.Add(signature)) {
        snapshots.Add(snapshot);
      }
      builders.Remove(key);
    }
    return snapshots;
  }

  private static TlogMissionItem? FromInt(MAVLink.mavlink_mission_item_int_t item) {
    if (item.mission_type != (byte)MAVLink.MAV_MISSION_TYPE.MISSION) {
      return null;
    }
    double scale = IsGlobalFrame(item.frame) ? 1e7 : 1e4;
    return new TlogMissionItem(
        item.seq, item.current, item.frame, item.command,
        item.param1, item.param2, item.param3, item.param4,
        item.x / scale, item.y / scale, item.z, item.autocontinue);
  }

  private static TlogMissionItem? FromFloat(MAVLink.mavlink_mission_item_t item) =>
      item.mission_type == (byte)MAVLink.MAV_MISSION_TYPE.MISSION
          ? new TlogMissionItem(
              item.seq, item.current, item.frame, item.command,
              item.param1, item.param2, item.param3, item.param4,
              item.x, item.y, item.z, item.autocontinue)
          : null;

  private static bool IsGlobalFrame(byte frame) => (MAVLink.MAV_FRAME)frame is
      MAVLink.MAV_FRAME.GLOBAL or
      MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT or
      MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT or
      MAVLink.MAV_FRAME.GLOBAL_INT or
      MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT or
      MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT_INT;

  private static void WriteQgcWpl(IReadOnlyList<TlogMissionItem> mission, string output) {
    using var writer = new StreamWriter(output, false, new UTF8Encoding(false));
    writer.WriteLine("QGC WPL 110");
    foreach (var item in mission) {
      writer.WriteLine(string.Join("\t", new[] {
        item.Sequence.ToString(CultureInfo.InvariantCulture),
        item.Current.ToString(CultureInfo.InvariantCulture),
        item.Frame.ToString(CultureInfo.InvariantCulture),
        item.Command.ToString(CultureInfo.InvariantCulture),
        item.Param1.ToString("0.000000", CultureInfo.InvariantCulture),
        item.Param2.ToString("0.000000", CultureInfo.InvariantCulture),
        item.Param3.ToString("0.000000", CultureInfo.InvariantCulture),
        item.Param4.ToString("0.000000", CultureInfo.InvariantCulture),
        item.X.ToString("0.0000000", CultureInfo.InvariantCulture),
        item.Y.ToString("0.0000000", CultureInfo.InvariantCulture),
        item.Z.ToString("0.000000", CultureInfo.InvariantCulture),
        item.AutoContinue.ToString(CultureInfo.InvariantCulture),
      }));
    }
  }

  private static string MissionSignature(TlogMissionItem item) => string.Join(",", new[] {
    item.Sequence.ToString(CultureInfo.InvariantCulture),
    item.Current.ToString(CultureInfo.InvariantCulture),
    item.Frame.ToString(CultureInfo.InvariantCulture),
    item.Command.ToString(CultureInfo.InvariantCulture),
    item.Param1.ToString("R", CultureInfo.InvariantCulture),
    item.Param2.ToString("R", CultureInfo.InvariantCulture),
    item.Param3.ToString("R", CultureInfo.InvariantCulture),
    item.Param4.ToString("R", CultureInfo.InvariantCulture),
    item.X.ToString("R", CultureInfo.InvariantCulture),
    item.Y.ToString("R", CultureInfo.InvariantCulture),
    item.Z.ToString("R", CultureInfo.InvariantCulture),
    item.AutoContinue.ToString(CultureInfo.InvariantCulture),
  });

  private static IEnumerable<MAVLink.MAVLinkMessage> ReadPackets(string path) {
    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var parser = new MAVLink.MavlinkParse(true);
    while (stream.Position < stream.Length) {
      MAVLink.MAVLinkMessage? packet;
      long before = stream.Position;
      try {
        packet = parser.ReadPacket(stream);
      } catch (EndOfStreamException) {
        yield break;
      } catch {
        if (stream.Position == before) {
          stream.Position = before + 1;
        }
        continue;
      }
      if (packet != null) {
        yield return packet;
      }
    }
  }

  private sealed class MissionBuilder {
    public MissionBuilder(ushort expected) => Expected = expected;

    public ushort Expected { get; }
    public Dictionary<ushort, TlogMissionItem> Items { get; } = new();
  }
}
