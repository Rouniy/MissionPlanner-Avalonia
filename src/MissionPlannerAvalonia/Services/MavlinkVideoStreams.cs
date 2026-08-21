using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MissionPlannerAvalonia.Services;

internal sealed record MavlinkVideoStreamOption(
    byte SystemId,
    byte ComponentId,
    byte StreamId,
    string Name,
    string Uri,
    string Source,
    string Details) {
  public override string ToString() => Name;
}

internal static partial class MavlinkVideoStreams {
  [GeneratedRegex(@":(\d+)(?:/|$)")]
  private static partial Regex PortRegex();

  internal static IReadOnlyList<MavlinkVideoStreamOption> Snapshot(
      IEnumerable<KeyValuePair<(byte SystemId, byte ComponentId, byte StreamId),
          MAVLink.mavlink_video_stream_information_t>> streams) => streams
      .Select(pair => TryCreate(pair.Key, pair.Value, out var option) ? option : null)
      .OfType<MavlinkVideoStreamOption>()
      .OrderBy(option => option.SystemId)
      .ThenBy(option => option.ComponentId)
      .ThenBy(option => option.StreamId)
      .ToArray();

  internal static bool TryCreate(
      (byte SystemId, byte ComponentId, byte StreamId) key,
      MAVLink.mavlink_video_stream_information_t stream,
      out MavlinkVideoStreamOption? option) {
    option = null;
    string uri = Decode(stream.uri);
    if (!TryResolveSource(stream, uri, out string source)) {
      return false;
    }

    string announcedName = Decode(stream.name);
    string name = string.IsNullOrWhiteSpace(announcedName)
        ? $"Camera {key.SystemId}:{key.ComponentId} stream {key.StreamId}"
        : announcedName;
    string type = ((MAVLink.VIDEO_STREAM_TYPE)stream.type).ToString();
    string details = $"MAVLink {key.SystemId}:{key.ComponentId}, stream {key.StreamId} · {type}"
        + $" · {stream.resolution_h}×{stream.resolution_v}"
        + $" · {stream.framerate:0.##} fps · {uri}";
    option = new MavlinkVideoStreamOption(
        key.SystemId, key.ComponentId, key.StreamId, name, uri, source, details);
    return true;
  }

  internal static bool TryResolveSource(
      MAVLink.mavlink_video_stream_information_t stream, string uri, out string source) {
    source = "";
    if (string.IsNullOrWhiteSpace(uri)) {
      return false;
    }
    if (uri.StartsWith("gst://", StringComparison.OrdinalIgnoreCase)) {
      source = MissionPlanner.ArduPilot.Mavlink.CameraProtocol.GStreamerPipeline(stream);
      return !string.IsNullOrWhiteSpace(source);
    }

    switch ((MAVLink.VIDEO_STREAM_TYPE)stream.type) {
      case MAVLink.VIDEO_STREAM_TYPE.RTSP:
        source = "rtsp://" + StripScheme(uri);
        return Uri.TryCreate(source, UriKind.Absolute, out _);
      case MAVLink.VIDEO_STREAM_TYPE.RTPUDP:
        source = MissionPlanner.ArduPilot.Mavlink.CameraProtocol.GStreamerPipeline(stream);
        return !string.IsNullOrWhiteSpace(source);
      case MAVLink.VIDEO_STREAM_TYPE.TCP_MPEG:
        source = "tcp://" + StripScheme(uri);
        return Uri.TryCreate(source, UriKind.Absolute, out _);
      case MAVLink.VIDEO_STREAM_TYPE.MPEG_TS:
        if (!TryPort(uri, out int port)) {
          return false;
        }
        source = $"udp://@:{port}";
        return true;
      default:
        return false;
    }
  }

  private static string Decode(byte[]? bytes) => bytes == null
      ? ""
      : Encoding.UTF8.GetString(bytes).Split('\0', 2)[0].Trim();

  private static string StripScheme(string uri) {
    int separator = uri.IndexOf("://", StringComparison.Ordinal);
    return separator >= 0 ? uri[(separator + 3)..] : uri;
  }

  private static bool TryPort(string uri, out int port) {
    if (int.TryParse(uri, out port) && port is >= 1 and <= 65535) {
      return true;
    }
    var match = PortRegex().Match(uri);
    return match.Success && int.TryParse(match.Groups[1].Value, out port)
        && port is >= 1 and <= 65535;
  }
}
