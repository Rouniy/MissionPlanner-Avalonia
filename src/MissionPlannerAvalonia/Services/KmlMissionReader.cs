using System;
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
  // SharpKml's vendored TypeBrowser cache is a process-wide Dictionary without locking.
  // Keep imports serialized so simultaneous UI/test imports cannot corrupt its lazy cache.
  private static readonly object _sharpKmlLock = new();

  internal static KmlMissionContent Read(string path) => ReadPath(path, Parse);

  internal static ImportedMapOverlay ReadOverlay(string path) {
    if (Path.GetExtension(path).Equals(".kmz", StringComparison.OrdinalIgnoreCase)) {
      using var archive = ZipFile.OpenRead(path);
      ZipArchiveEntry entry = archive.Entries.FirstOrDefault(candidate =>
          Path.GetExtension(candidate.FullName).Equals(".kml",
              StringComparison.OrdinalIgnoreCase))
          ?? throw new InvalidDataException("The KMZ archive contains no KML document.");
      using var stream = entry.Open();
      return ParseOverlay(stream, href => ReadKmzResource(archive, entry, href));
    }

    string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
    using var file = File.OpenRead(path);
    return ParseOverlay(file, href => ReadLocalResource(directory, href));
  }

  private static TResult ReadPath<TResult>(string path, Func<Stream, TResult> parse) {
    if (Path.GetExtension(path).Equals(".kmz", System.StringComparison.OrdinalIgnoreCase)) {
      using var archive = ZipFile.OpenRead(path);
      var entry = archive.Entries.FirstOrDefault(candidate =>
          Path.GetExtension(candidate.FullName).Equals(".kml",
              System.StringComparison.OrdinalIgnoreCase));
      if (entry == null) {
        throw new InvalidDataException("The KMZ archive contains no KML document.");
      }
      using var stream = entry.Open();
      return parse(stream);
    }

    using var file = File.OpenRead(path);
    return parse(file);
  }

  internal static KmlMissionContent Parse(Stream stream) {
    KmlFile kml = LoadKml(stream);
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

  internal static ImportedMapOverlay ParseOverlay(Stream stream) =>
      ParseOverlay(stream, _ => null);

  private static ImportedMapOverlay ParseOverlay(
      Stream stream,
      Func<string, byte[]?> readResource) {
    KmlFile kml = LoadKml(stream);
    var routes = new List<ImportedOverlayRoute>();
    var markers = new List<ImportedOverlayMarker>();
    var rasters = new List<ImportedOverlayRaster>();
    Feature? feature = kml.Root switch {
      Kml root => root.Feature,
      Feature rootFeature => rootFeature,
      _ => null,
    };
    if (feature != null) {
      AddFeature(feature, null, routes, markers, rasters, readResource);
    }
    return new ImportedMapOverlay(routes, markers, rasters);
  }

  private static KmlFile LoadKml(Stream stream) {
    lock (_sharpKmlLock) {
      return KmlFile.Load(stream);
    }
  }

  private static void AddFeature(
      Feature feature,
      Document? styleRoot,
      ICollection<ImportedOverlayRoute> routes,
      ICollection<ImportedOverlayMarker> markers,
      ICollection<ImportedOverlayRaster> rasters,
      Func<string, byte[]?> readResource) {
    switch (feature) {
      case Document document:
        foreach (Feature child in document.Features) {
          AddFeature(child, document, routes, markers, rasters, readResource);
        }
        return;
      case Folder folder:
        foreach (Feature child in folder.Features) {
          AddFeature(child, styleRoot, routes, markers, rasters, readResource);
        }
        return;
      case GroundOverlay groundOverlay:
        try {
          ImportedOverlayRaster? raster =
              KmlGroundOverlayProcessor.Create(groundOverlay, readResource);
          if (raster != null) {
            rasters.Add(raster);
          }
        } catch {
          // A missing or malformed image does not abort vector geometry in the KML.
        }
        return;
      case Placemark { Geometry: not null } placemark:
        KmlRouteStyle style = ResolveLineStyle(placemark.StyleUrl, styleRoot);
        AddGeometry(placemark.Geometry, placemark.Name, style, routes, markers);
        return;
    }
  }

  private static byte[]? ReadKmzResource(
      ZipArchive archive,
      ZipArchiveEntry kmlEntry,
      string href) {
    string resource = DecodeResourceName(href).Replace('\\', '/');
    if (string.IsNullOrWhiteSpace(resource)
        || resource.StartsWith("/", StringComparison.Ordinal)
        || Uri.TryCreate(resource, UriKind.Absolute, out _)) {
      return null;
    }

    string[] resourceParts = resource.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (resourceParts.Any(part => part == "..")) {
      return null;
    }
    string? entryDirectory = GetArchiveDirectory(kmlEntry.FullName);
    string candidate = string.Join('/',
        ((entryDirectory == null ? Array.Empty<string>() : [entryDirectory])
            .Concat(resourceParts.Where(part => part != "."))));
    ZipArchiveEntry? image = archive.Entries.FirstOrDefault(entry =>
        string.Equals(entry.FullName.Replace('\\', '/'), candidate,
            StringComparison.OrdinalIgnoreCase));
    return image == null ? null : ReadArchiveEntry(image);
  }

  private static byte[]? ReadArchiveEntry(ZipArchiveEntry entry) {
    if (entry.Length <= 0 || entry.Length > KmlGroundOverlayProcessor.MaximumResourceBytes) {
      return null;
    }
    using Stream source = entry.Open();
    using var output = new MemoryStream((int)entry.Length);
    source.CopyTo(output);
    return output.Length <= KmlGroundOverlayProcessor.MaximumResourceBytes
        ? output.ToArray()
        : null;
  }

  private static byte[]? ReadLocalResource(string directory, string href) {
    string resource = DecodeResourceName(href);
    if (string.IsNullOrWhiteSpace(resource)) {
      return null;
    }

    string path;
    if (Uri.TryCreate(resource, UriKind.Absolute, out Uri? uri)) {
      if (!uri.IsFile) {
        return null;
      }
      path = uri.LocalPath;
    } else {
      path = Path.IsPathRooted(resource)
          ? resource
          : Path.Combine(directory, resource.Replace('/', Path.DirectorySeparatorChar));
    }

    var info = new FileInfo(path);
    return !info.Exists || info.Length <= 0
           || info.Length > KmlGroundOverlayProcessor.MaximumResourceBytes
        ? null
        : File.ReadAllBytes(info.FullName);
  }

  private static string DecodeResourceName(string href) {
    string value = href.Trim();
    int query = value.IndexOfAny(['?', '#']);
    if (query >= 0) {
      value = value[..query];
    }
    try {
      return Uri.UnescapeDataString(value);
    } catch (UriFormatException) {
      return "";
    }
  }

  private static string? GetArchiveDirectory(string fullName) {
    string normalized = fullName.Replace('\\', '/');
    int slash = normalized.LastIndexOf('/');
    return slash <= 0 ? null : normalized[..slash];
  }

  private static void AddGeometry(
      Geometry geometry,
      string? name,
      KmlRouteStyle style,
      ICollection<ImportedOverlayRoute> routes,
      ICollection<ImportedOverlayMarker> markers) {
    switch (geometry) {
      case SharpKml.Dom.Point { Coordinate: not null } point:
        ImportedGeoPoint markerPoint = ToGeoPoint(point.Coordinate);
        if (IsValid(markerPoint)) {
          markers.Add(new ImportedOverlayMarker(name ?? "", markerPoint, ShowLabel: true));
        }
        return;
      case LineString { Coordinates: not null } line:
        AddRoute(line.Coordinates, name ?? "kmlroute", false, style, routes);
        return;
      case Polygon polygon when polygon.OuterBoundary?.LinearRing?.Coordinates != null:
        AddRoute(polygon.OuterBoundary.LinearRing.Coordinates,
            name ?? "kmlpolygon", true, style, routes);
        return;
      case LinearRing { Coordinates: not null } ring:
        AddRoute(ring.Coordinates, name ?? "kmlring", true, style, routes);
        return;
      case MultipleGeometry multiple:
        foreach (Geometry child in multiple.Geometry) {
          // Upstream synthesizes a new Placemark with only Geometry and StyleUrl.
          AddGeometry(child, null, style, routes, markers);
        }
        return;
    }
  }

  private static void AddRoute(
      IEnumerable<Vector> coordinates,
      string name,
      bool closed,
      KmlRouteStyle style,
      ICollection<ImportedOverlayRoute> routes) {
    ImportedGeoPoint[] points = coordinates.Select(ToGeoPoint).Where(IsValid).ToArray();
    if (points.Length >= 2) {
      routes.Add(new ImportedOverlayRoute(name, points, style.Color, closed, style.Width));
    }
  }

  private static KmlRouteStyle ResolveLineStyle(Uri? styleUrl, Document? root) {
    var fallback = new KmlRouteStyle(new ImportedMapColor(255, 255, 255), 2);
    if (styleUrl == null || root == null) {
      return fallback;
    }
    string styleMapId = styleUrl.OriginalString.TrimStart('#');
    StyleMapCollection? styleMap = root.Styles
        .FirstOrDefault(selector => selector.Id == styleMapId) as StyleMapCollection;
    Uri? mappedUrl = styleMap?.FirstOrDefault()?.StyleUrl;
    if (mappedUrl == null) {
      return fallback;
    }
    string styleId = mappedUrl.OriginalString.TrimStart('#');
    Style? style = root.Styles.FirstOrDefault(selector => selector.Id == styleId) as Style;
    if (style?.Line == null) {
      return fallback;
    }
    Color32 color = style.Line.Color ?? ColorStyle.DefaultColor;
    double width = style.Line.Width.HasValue ? (int)style.Line.Width.Value : 2;
    return new KmlRouteStyle(
        new ImportedMapColor(color.Red, color.Green, color.Blue, color.Alpha), width);
  }

  private static ImportedGeoPoint ToGeoPoint(Vector vector) =>
      new(vector.Latitude, vector.Longitude, vector.Altitude ?? 0);

  private static bool IsValid(ImportedGeoPoint point) =>
      double.IsFinite(point.Lat) && double.IsFinite(point.Lng) &&
      point.Lat is >= -90 and <= 90 && point.Lng is >= -180 and <= 180;

  private readonly record struct KmlRouteStyle(ImportedMapColor Color, double Width);

  private static KmlMissionPoint ToPoint(Vector vector) =>
      new(vector.Latitude, vector.Longitude, vector.Altitude ?? 0);
}
