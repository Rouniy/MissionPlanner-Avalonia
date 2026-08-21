using System.Xml.Linq;
using MissionPlannerAvalonia.Services;

namespace MissionPlannerAvalonia.Tests;

public class CotEventSerializerTests {
  [Fact]
  public void Serialize_creates_invariant_cot_event() {
    var timestamp = new DateTime(2026, 8, 21, 12, 34, 56, DateTimeKind.Utc);
    var xml = CotEventSerializer.Serialize(
        "mp-1-1", "a-f-A-M-F-Q", 35.1234567, 33.7654321, 123.45, 270.5, 12.25,
        "Copter-1", timestampUtc: timestamp);
    var root = XDocument.Parse(xml).Root!;

    Assert.Equal("event", root.Name.LocalName);
    Assert.Equal("mp-1-1", root.Attribute("uid")?.Value);
    Assert.Equal("2026-08-21T12:34:56.000Z", root.Attribute("time")?.Value);
    Assert.Equal("35.1234567", root.Element("point")?.Attribute("lat")?.Value);
    Assert.Equal("33.7654321", root.Element("point")?.Attribute("lon")?.Value);
    Assert.Equal("Copter-1", root.Element("detail")?.Element("contact")?.Attribute("callsign")?.Value);
  }
}
