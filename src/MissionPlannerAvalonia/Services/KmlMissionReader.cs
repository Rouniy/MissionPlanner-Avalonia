using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SharpKml.Base;
using SharpKml.Dom;
using SharpKml.Engine;

namespace MissionPlannerAvalonia.Services;

internal sealed record KmlMissionPoint(double Lat, double Lng, double Alt);

internal sealed record KmlMissionContent(
    IReadOnlyList<KmlMissionPoint> Route,
    IReadOnlyList<PoiPoint> Pois,
    IReadOnlyList<KmlMissionPoint> Overlay);

internal static class KmlMissionReader {
  internal static KmlMissionContent Read(string path) {
    if (Path.GetExtension(path).Equals(".kmz", System.StringComparison.OrdinalIgnoreCase)) {
      using var archive = ZipFile.OpenRead(path);
      var entry = archive.Entries.FirstOrDefault(candidate =>
          Path.GetExtension(candidate.FullName).Equals(".kml",
              System.StringComparison.OrdinalIgnoreCase));
      if (entry == null) {
        throw new InvalidDataException("The KMZ archive contains no KML document.");
      }
      using var stream = entry.Open();
      return Parse(stream);
    }

    using var file = File.OpenRead(path);
    return Parse(file);
  }

  internal static KmlMissionContent Parse(Stream stream) {
    KmlFile kml = KmlFile.Load(stream);
    var route = new List<KmlMissionPoint>();
    var pois = new List<PoiPoint>();
    var overlay = new List<KmlMissionPoint>();
    if (kml.Root == null) {
      return new KmlMissionContent(route, pois, overlay);
    }

    foreach (Element element in kml.Root.Flatten()) {
      switch (element) {
        case Placemark { Geometry: SharpKml.Dom.Point point } placemark
            when point.Coordinate != null:
          var coordinate = point.Coordinate;
          pois.Add(new PoiPoint(coordinate.Latitude, coordinate.Longitude,
              coordinate.Altitude ?? 0, placemark.Name ?? "POI"));
          overlay.Add(ToPoint(coordinate));
          break;
        case LineString line when line.Coordinates != null:
          foreach (Vector vector in line.Coordinates) {
            var missionPoint = ToPoint(vector);
            route.Add(missionPoint);
            overlay.Add(missionPoint);
          }
          break;
        case LinearRing ring when ring.Coordinates != null:
          overlay.AddRange(ring.Coordinates.Select(ToPoint));
          break;
      }
    }

    return new KmlMissionContent(route, pois, overlay);
  }

  private static KmlMissionPoint ToPoint(Vector vector) =>
      new(vector.Latitude, vector.Longitude, vector.Altitude ?? 0);
}
