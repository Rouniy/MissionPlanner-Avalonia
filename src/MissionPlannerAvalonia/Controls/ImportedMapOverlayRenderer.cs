using System;
using System.Collections.Generic;
using System.Linq;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using MissionPlannerAvalonia.Services;
using NetTopologySuite.Geometries;

namespace MissionPlannerAvalonia.Controls;

internal static class ImportedMapOverlayRenderer {
  internal static IReadOnlyList<MPoint> Populate(
      WritableLayer vectors,
      WritableLayer rasters,
      ImportedMapOverlay overlay) {
    vectors.Clear();
    rasters.Clear();
    var extentPoints = new List<MPoint>();

    foreach (ImportedOverlayRoute route in overlay.Routes) {
      var points = route.Points.Select(Project).ToList();
      if (route.Closed && points.Count > 2 && !points[0].Equals(points[^1])) {
        points.Add(points[0]);
      }
      if (points.Count < 2) {
        continue;
      }
      extentPoints.AddRange(points);
      var feature = new GeometryFeature {
        Geometry = new LineString(points.Select(point =>
            new Coordinate(point.X, point.Y)).ToArray()),
      };
      feature.Styles.Add(new VectorStyle {
        Line = new Pen(new Color(route.Color.R, route.Color.G,
            route.Color.B, route.Color.A), route.Width),
      });
      vectors.Add(feature);
    }

    foreach (ImportedOverlayMarker marker in overlay.Markers) {
      MPoint point = Project(marker.Point);
      extentPoints.Add(point);
      var feature = new PointFeature(point);
      feature.Styles.Add(new SymbolStyle {
        SymbolType = SymbolType.Ellipse,
        Fill = new Brush(new Color(0xFF, 0x30, 0x30)),
        Outline = new Pen(Color.White, 1),
        SymbolScale = 0.5,
      });
      if (marker.ShowLabel && !string.IsNullOrWhiteSpace(marker.Name)) {
        feature.Styles.Add(new LabelStyle {
          Text = marker.Name,
          ForeColor = Color.White,
          BackColor = new Brush(new Color(0, 0, 0, 140)),
          Font = new Font { Size = 10 },
          Offset = new Offset(0, 14),
        });
      }
      vectors.Add(feature);
    }

    foreach (ImportedOverlayRaster raster in overlay.Rasters) {
      var southWest = SphericalMercator.FromLonLat(raster.West, raster.South);
      var northEast = SphericalMercator.FromLonLat(raster.East, raster.North);
      var extent = new MRect(southWest.x, southWest.y, northEast.x, northEast.y);
      var feature = new RasterFeature(new MRaster(raster.Png, extent));
      feature.Styles.Add(new RasterStyle());
      rasters.Add(feature);
      extentPoints.Add(new MPoint(southWest.x, southWest.y));
      extentPoints.Add(new MPoint(northEast.x, northEast.y));
    }

    vectors.DataHasChanged();
    rasters.DataHasChanged();
    return extentPoints;
  }

  private static MPoint Project(ImportedGeoPoint point) {
    var (x, y) = SphericalMercator.FromLonLat(point.Lng, point.Lat);
    return new MPoint(x, y);
  }
}
