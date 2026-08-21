using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpKml.Dom;
using SkiaSharp;

namespace MissionPlannerAvalonia.Services;

internal static class KmlGroundOverlayProcessor {
  internal const long MaximumResourceBytes = 64L * 1024 * 1024;
  internal const long MaximumSourcePixels = 64L * 1024 * 1024;
  internal const int MaximumOutputDimension = 4096;

  internal static ImportedOverlayRaster? Create(
      GroundOverlay overlay,
      Func<string, byte[]?> readResource) {
    string? href = overlay.Icon?.Href?.OriginalString;
    if (string.IsNullOrWhiteSpace(href)) {
      return null;
    }

    IReadOnlyList<ImportedGeoPoint>? corners = overlay.GXLatLonQuad?.Coordinates?
        .Select(ToPoint).ToArray() ?? ConvertLatLonBox(overlay.Bounds);
    if (corners is not { Count: 4 } || corners.Any(point => !IsValid(point))) {
      return null;
    }

    double north = corners.Max(point => point.Lat);
    double south = corners.Min(point => point.Lat);
    double east = corners.Max(point => point.Lng);
    double west = corners.Min(point => point.Lng);
    if (!double.IsFinite(north) || !double.IsFinite(south)
        || !double.IsFinite(east) || !double.IsFinite(west)
        || north <= south || east <= west || east - west > 180) {
      return null;
    }

    byte[]? source = readResource(href);
    if (source is not { Length: > 0 } || source.LongLength > MaximumResourceBytes) {
      return null;
    }

    byte[]? png = WarpToBoundingRectangle(source, corners, north, south, east, west);
    return png == null
        ? null
        : new ImportedOverlayRaster(overlay.Name ?? "GroundOverlay", png,
            north, south, east, west);
  }

  internal static IReadOnlyList<ImportedGeoPoint>? ConvertLatLonBox(LatLonBox? box) {
    if (box?.North is not { } north || box.South is not { } south
        || box.East is not { } east || box.West is not { } west) {
      return null;
    }
    if (!new[] { north, south, east, west }.All(double.IsFinite)
        || north <= south || east <= west) {
      return null;
    }

    double centerLat = (north + south) / 2;
    double centerLng = (east + west) / 2;
    var corners = new[] {
      new ImportedGeoPoint(south, west),
      new ImportedGeoPoint(south, east),
      new ImportedGeoPoint(north, east),
      new ImportedGeoPoint(north, west),
    };

    double rotation = box.Rotation ?? 0;
    if (!double.IsFinite(rotation)) {
      return null;
    }
    if (Math.Abs(rotation) <= 0.001) {
      return corners;
    }

    // This intentionally follows FlightPlanner.ConvertLatLonBoxToLatLonQuad,
    // including its longitude scaling and 0.001-degree rotation threshold.
    double rotationRadians = -rotation * Math.PI / 180;
    double cosTheta = Math.Cos(rotationRadians);
    double sinTheta = Math.Sin(rotationRadians);
    double cosLatitude = Math.Cos(centerLat * Math.PI / 180);
    if (Math.Abs(cosLatitude) < 1e-9) {
      return null;
    }

    return corners.Select(corner => {
      double latitude = corner.Lat - centerLat;
      double longitudeScaled = (corner.Lng - centerLng) * cosLatitude;
      double rotatedLatitude = latitude * cosTheta - longitudeScaled * sinTheta;
      double rotatedLongitudeScaled = latitude * sinTheta + longitudeScaled * cosTheta;
      return new ImportedGeoPoint(
          rotatedLatitude + centerLat,
          rotatedLongitudeScaled / cosLatitude + centerLng);
    }).ToArray();
  }

  internal static byte[]? WarpToBoundingRectangle(
      byte[] sourceBytes,
      IReadOnlyList<ImportedGeoPoint> corners,
      double north,
      double south,
      double east,
      double west) {
    if (corners.Count != 4 || sourceBytes.LongLength > MaximumResourceBytes) {
      return null;
    }

    try {
      using (var input = new MemoryStream(sourceBytes, writable: false))
      using (SKCodec? codec = SKCodec.Create(input)) {
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0
            || (long)codec.Info.Width * codec.Info.Height > MaximumSourcePixels) {
          return null;
        }
      }

      using SKBitmap? source = SKBitmap.Decode(sourceBytes);
      if (source == null) {
        return null;
      }

      double widthDegrees = east - west;
      double heightDegrees = north - south;
      double aspectRatio = widthDegrees / heightDegrees;
      if (!double.IsFinite(aspectRatio) || aspectRatio <= 0) {
        return null;
      }

      int maxDimension = Math.Min(Math.Max(source.Width, source.Height),
          MaximumOutputDimension);
      int outputWidth;
      int outputHeight;
      if (aspectRatio > 1) {
        outputWidth = maxDimension;
        outputHeight = Math.Max(1, (int)(maxDimension / aspectRatio));
      } else {
        outputHeight = maxDimension;
        outputWidth = Math.Max(1, (int)(maxDimension * aspectRatio));
      }
      if (outputWidth <= 0 || outputHeight <= 0) {
        return null;
      }

      var destination = corners.Select(corner => new SKPoint(
          (float)((corner.Lng - west) / widthDegrees * outputWidth),
          (float)((north - corner.Lat) / heightDegrees * outputHeight))).ToArray();
      if (destination.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y))) {
        return null;
      }

      using var output = new SKBitmap(new SKImageInfo(outputWidth, outputHeight,
          SKColorType.Bgra8888, SKAlphaType.Premul));
      using var canvas = new SKCanvas(output);
      canvas.Clear(SKColors.Transparent);
      using var paint = new SKPaint { IsAntialias = true };
      using SKImage sourceImage = SKImage.FromBitmap(source);

      // System.Drawing.DrawImage(PointF[3]) maps source UL, UR and LL. Upstream passes
      // quad indexes 3, 2 and 0 respectively; index 1 is deliberately ignored.
      SKPoint upperLeft = destination[3];
      SKPoint upperRight = destination[2];
      SKPoint lowerLeft = destination[0];
      var matrix = new SKMatrix(
          (upperRight.X - upperLeft.X) / source.Width,
          (lowerLeft.X - upperLeft.X) / source.Height,
          upperLeft.X,
          (upperRight.Y - upperLeft.Y) / source.Width,
          (lowerLeft.Y - upperLeft.Y) / source.Height,
          upperLeft.Y,
          0, 0, 1);
      var sampling = new SKSamplingOptions(new SKCubicResampler(1f / 3, 1f / 3));
      try {
        canvas.SetMatrix(matrix);
        canvas.DrawImage(sourceImage, 0, 0, sampling, paint);
      } catch {
        // Mission Planner falls back to an untransformed image if its
        // parallelogram draw fails, rather than discarding the overlay.
        canvas.ResetMatrix();
        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(sourceImage,
            new SKRect(0, 0, outputWidth, outputHeight), sampling, paint);
      }
      canvas.Flush();

      using SKImage image = SKImage.FromBitmap(output);
      using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
      return encoded.ToArray();
    } catch {
      // A malformed image affects only its own GroundOverlay, matching upstream.
      return null;
    }
  }

  private static ImportedGeoPoint ToPoint(SharpKml.Base.Vector vector) =>
      new(vector.Latitude, vector.Longitude);

  private static bool IsValid(ImportedGeoPoint point) =>
      double.IsFinite(point.Lat) && double.IsFinite(point.Lng)
      && point.Lat is >= -90 and <= 90 && point.Lng is >= -180 and <= 180;
}
