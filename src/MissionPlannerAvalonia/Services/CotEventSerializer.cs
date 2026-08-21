using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using MissionPlanner.Utilities.CoT;

namespace MissionPlannerAvalonia.Services;

public sealed record CotIdentityDetail(
    bool IncludeTakv = false,
    string? Callsign = null,
    string? Endpoint = null,
    string? Vmf = null);

public static class CotEventSerializer {
  private static readonly XmlSerializer _serializer = new(typeof(@event));
  private static readonly XmlSerializerNamespaces _namespaces = EmptyNamespaces();

  public static string Serialize(
      string uid, string type, double latitude, double longitude, double altitude,
      double course, double speed, string? callsign = null, bool indent = false,
      DateTime? timestampUtc = null, CotIdentityDetail? identity = null) {
    var time = (timestampUtc ?? DateTime.UtcNow).ToUniversalTime();
    const string timeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    var details = new detail {
      track = new track {
        course = course.ToString("0.00", CultureInfo.InvariantCulture),
        speed = speed.ToString("0.00", CultureInfo.InvariantCulture),
      },
    };
    if (identity?.IncludeTakv == true) {
      details.takv = new takv();
    }
    string? contactCallsign = identity?.Callsign is { Length: > 0 }
        ? identity.Callsign
        : !string.IsNullOrWhiteSpace(callsign) ? callsign.Trim() : null;
    string? endpoint = identity?.Endpoint is { Length: > 0 }
        ? identity.Endpoint
        : null;
    if (contactCallsign != null || endpoint != null) {
      details.contact = new contact {
        callsign = contactCallsign,
        endpoint = endpoint,
      };
    }
    if (identity?.Vmf is { Length: > 0 }) {
      details.uid = new uid { vmf = identity.Vmf };
    }

    var cot = new @event {
      uid = uid ?? "",
      type = type ?? "",
      time = time.ToString(timeFormat, CultureInfo.InvariantCulture),
      start = time.AddSeconds(-5).ToString(timeFormat, CultureInfo.InvariantCulture),
      stale = time.AddSeconds(120).ToString(timeFormat, CultureInfo.InvariantCulture),
      how = "m-g",
      detail = details,
      point = new point {
        lat = latitude.ToString("0.0000000", CultureInfo.InvariantCulture),
        lon = longitude.ToString("0.0000000", CultureInfo.InvariantCulture),
        hae = altitude.ToString("0.00", CultureInfo.InvariantCulture),
      },
    };

    var settings = new XmlWriterSettings {
      OmitXmlDeclaration = true,
      Indent = indent,
      Encoding = Encoding.UTF8,
      NewLineOnAttributes = indent,
    };
    using var writer = new Utf8StringWriter();
    using (var xml = XmlWriter.Create(writer, settings)) {
      _serializer.Serialize(xml, cot, _namespaces);
    }
    return writer.ToString();
  }

  private static XmlSerializerNamespaces EmptyNamespaces() {
    var namespaces = new XmlSerializerNamespaces();
    namespaces.Add("", "");
    return namespaces;
  }

  private sealed class Utf8StringWriter : StringWriter {
    public override Encoding Encoding => Encoding.UTF8;
  }
}
