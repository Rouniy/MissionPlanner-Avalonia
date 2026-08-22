using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using BruTile.Predefined;
using Mapsui.Projections;
using SkiaSharp;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct Terrain3DGeoPoint(
    double Latitude,
    double Longitude,
    double AltitudeM);

internal readonly record struct Terrain3DWaypoint(
    int Sequence,
    Terrain3DGeoPoint Position,
    string Label,
    Terrain3DWaypointKind Kind = Terrain3DWaypointKind.Mission);

internal enum Terrain3DWaypointKind {
  Mission,
  Guided,
  Target,
  Vehicle,
}

internal sealed record Terrain3DSnapshot(
    Terrain3DGeoPoint Vehicle,
    double RelativeAltitudeM,
    double RollDeg,
    double PitchDeg,
    double YawDeg,
    double VelocityNorthMps,
    double VelocityEastMps,
    double VelocityVerticalMps,
    DateTime CapturedUtc,
    string Mode,
    bool Armed,
    byte SystemId,
    byte ComponentId,
    IReadOnlyList<Terrain3DWaypoint> Waypoints) {
  internal bool HasPosition => Terrain3DGeoMath.IsValid(Vehicle);
}

internal readonly record struct Terrain3DSettings(
    double RangeM,
    int GridSize,
    int TextureMinZoom,
    int TextureMaxZoom,
    double VerticalExaggeration,
    bool FogEnabled,
    bool ImageryEnabled) {
  internal Terrain3DSettings Normalize() {
    int minimumZoom = Math.Clamp(TextureMinZoom, 1, 20);
    int maximumZoom = Math.Clamp(TextureMaxZoom, minimumZoom, 20);
    return new Terrain3DSettings(
        Math.Clamp(double.IsFinite(RangeM) ? RangeM : 1500, 250, 5000),
        Math.Clamp(GridSize | 1, 17, 65),
        minimumZoom,
        maximumZoom,
        Math.Clamp(double.IsFinite(VerticalExaggeration) ? VerticalExaggeration : 1, 0.25, 8),
        FogEnabled,
        ImageryEnabled);
  }
}

internal readonly record struct Terrain3DCamera(
    double EastM,
    double NorthM,
    double AltitudeM,
    double YawDeg,
    double PitchDeg,
    double RollDeg) {
  internal static Terrain3DCamera Locked(
      Terrain3DWorld world, Terrain3DSnapshot snapshot, DateTime nowUtc) {
    (double east, double north) = Terrain3DGeoMath.ToLocal(world.Center, snapshot.Vehicle);
    double seconds = Math.Clamp((nowUtc - snapshot.CapturedUtc).TotalSeconds, 0, 1);
    east += snapshot.VelocityEastMps * seconds;
    north += snapshot.VelocityNorthMps * seconds;
    double altitude = snapshot.Vehicle.AltitudeM + snapshot.VelocityVerticalMps * seconds;
    double terrain = world.SampleAltitude(east, north);
    if (double.IsFinite(terrain)) {
      altitude = Math.Max(altitude, terrain + 1);
    }
    return new Terrain3DCamera(
        east, north, altitude, snapshot.YawDeg, snapshot.PitchDeg, snapshot.RollDeg);
  }

  internal Terrain3DCamera Move(Terrain3DCameraMotion motion, double amount = 10) {
    double yaw = YawDeg * Math.PI / 180;
    double eastForward = Math.Sin(yaw);
    double northForward = Math.Cos(yaw);
    return motion switch {
      Terrain3DCameraMotion.Forward => this with {
        EastM = EastM + eastForward * amount,
        NorthM = NorthM + northForward * amount,
      },
      Terrain3DCameraMotion.Backward => this with {
        EastM = EastM - eastForward * amount,
        NorthM = NorthM - northForward * amount,
      },
      Terrain3DCameraMotion.Left => this with {
        EastM = EastM - northForward * amount,
        NorthM = NorthM + eastForward * amount,
      },
      Terrain3DCameraMotion.Right => this with {
        EastM = EastM + northForward * amount,
        NorthM = NorthM - eastForward * amount,
      },
      Terrain3DCameraMotion.Up => this with { AltitudeM = AltitudeM + amount },
      Terrain3DCameraMotion.Down => this with { AltitudeM = AltitudeM - amount },
      Terrain3DCameraMotion.YawLeft => this with { YawDeg = NormalizeAngle(YawDeg - amount) },
      Terrain3DCameraMotion.YawRight => this with { YawDeg = NormalizeAngle(YawDeg + amount) },
      Terrain3DCameraMotion.PitchUp => this with { PitchDeg = Math.Clamp(PitchDeg + amount, -85, 85) },
      Terrain3DCameraMotion.PitchDown => this with { PitchDeg = Math.Clamp(PitchDeg - amount, -85, 85) },
      _ => this,
    };
  }

  private static double NormalizeAngle(double value) {
    value %= 360;
    return value < 0 ? value + 360 : value;
  }
}

internal enum Terrain3DCameraMotion {
  Forward,
  Backward,
  Left,
  Right,
  Up,
  Down,
  YawLeft,
  YawRight,
  PitchUp,
  PitchDown,
}

internal readonly record struct Terrain3DVertex(
    double EastM,
    double NorthM,
    double AltitudeM,
    double Latitude,
    double Longitude);

internal sealed class Terrain3DWorld : IDisposable {
  internal Terrain3DWorld(
      Terrain3DGeoPoint center,
      double rangeM,
      int gridSize,
      Terrain3DVertex[] vertices,
      double minimumAltitudeM,
      double maximumAltitudeM,
      double referenceAltitudeM,
      int missingSamples,
      Terrain3DTexture? texture) {
    Center = center;
    RangeM = rangeM;
    GridSize = gridSize;
    Vertices = vertices;
    MinimumAltitudeM = minimumAltitudeM;
    MaximumAltitudeM = maximumAltitudeM;
    ReferenceAltitudeM = referenceAltitudeM;
    MissingSamples = missingSamples;
    Texture = texture;
  }

  internal Terrain3DGeoPoint Center { get; }
  internal double RangeM { get; }
  internal int GridSize { get; }
  internal Terrain3DVertex[] Vertices { get; }
  internal double MinimumAltitudeM { get; }
  internal double MaximumAltitudeM { get; }
  internal double ReferenceAltitudeM { get; }
  internal int MissingSamples { get; }
  internal Terrain3DTexture? Texture { get; }

  internal double SampleAltitude(double eastM, double northM) {
    double x = (eastM + RangeM) / (RangeM * 2) * (GridSize - 1);
    double y = (northM + RangeM) / (RangeM * 2) * (GridSize - 1);
    if (x < 0 || y < 0 || x > GridSize - 1 || y > GridSize - 1) {
      return double.NaN;
    }
    int x0 = Math.Clamp((int)Math.Floor(x), 0, GridSize - 1);
    int y0 = Math.Clamp((int)Math.Floor(y), 0, GridSize - 1);
    int x1 = Math.Min(x0 + 1, GridSize - 1);
    int y1 = Math.Min(y0 + 1, GridSize - 1);
    double tx = x - x0;
    double ty = y - y0;
    double top = Lerp(Vertices[y0 * GridSize + x0].AltitudeM,
        Vertices[y0 * GridSize + x1].AltitudeM, tx);
    double bottom = Lerp(Vertices[y1 * GridSize + x0].AltitudeM,
        Vertices[y1 * GridSize + x1].AltitudeM, tx);
    return Lerp(top, bottom, ty);
  }

  public void Dispose() => Texture?.Dispose();

  private static double Lerp(double a, double b, double amount) => a + (b - a) * amount;
}

internal sealed class Terrain3DTexture : IDisposable {
  internal Terrain3DTexture(
      SKBitmap bitmap,
      int zoom,
      int minimumColumn,
      int minimumRow,
      int columns,
      int rows,
      int loadedTiles,
      int requestedTiles) {
    Bitmap = bitmap;
    Zoom = zoom;
    MinimumColumn = minimumColumn;
    MinimumRow = minimumRow;
    Columns = columns;
    Rows = rows;
    LoadedTiles = loadedTiles;
    RequestedTiles = requestedTiles;
  }

  internal SKBitmap Bitmap { get; }
  internal int Zoom { get; }
  internal int MinimumColumn { get; }
  internal int MinimumRow { get; }
  internal int Columns { get; }
  internal int Rows { get; }
  internal int LoadedTiles { get; }
  internal int RequestedTiles { get; }

  internal SKPoint Coordinate(double latitude, double longitude) {
    (double pixelX, double pixelY) = Terrain3DGeoMath.WebMercatorPixel(
        latitude, longitude, Zoom);
    return new SKPoint(
        (float)(pixelX - MinimumColumn * 256d),
        (float)(pixelY - MinimumRow * 256d));
  }

  public void Dispose() => Bitmap.Dispose();
}

internal static class Terrain3DWorldBuilder {
  internal const int MaximumTextureTiles = 64;

  internal static Terrain3DWorld BuildMesh(
      Terrain3DSnapshot snapshot,
      Terrain3DSettings settings,
      Func<double, double, double?> elevation,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(elevation);
    Terrain3DSettings normalized = settings.Normalize();
    if (!snapshot.HasPosition) {
      throw new ArgumentException("A valid vehicle position is required.", nameof(snapshot));
    }

    var center = snapshot.Vehicle;
    int size = normalized.GridSize;
    double spacing = normalized.RangeM * 2 / (size - 1);
    double fallback = double.IsFinite(snapshot.RelativeAltitudeM)
        ? snapshot.Vehicle.AltitudeM - snapshot.RelativeAltitudeM
        : snapshot.Vehicle.AltitudeM;
    if (!double.IsFinite(fallback)) {
      fallback = 0;
    }

    var vertices = new Terrain3DVertex[size * size];
    var rawAltitudes = new double?[vertices.Length];
    var validAltitudes = new List<double>(vertices.Length);
    int missing = 0;
    for (int row = 0; row < size; row++) {
      cancellationToken.ThrowIfCancellationRequested();
      double north = -normalized.RangeM + row * spacing;
      for (int column = 0; column < size; column++) {
        double east = -normalized.RangeM + column * spacing;
        Terrain3DGeoPoint geo = Terrain3DGeoMath.Offset(center, east, north, 0);
        double? altitude = elevation(geo.Latitude, geo.Longitude);
        if (altitude is { } value && double.IsFinite(value)) {
          rawAltitudes[row * size + column] = value;
          validAltitudes.Add(value);
        } else {
          missing++;
        }
        vertices[row * size + column] = new Terrain3DVertex(
            east, north, 0, geo.Latitude, geo.Longitude);
      }
    }

    double replacement = validAltitudes.Count == 0
        ? fallback
        : validAltitudes.Order().ElementAt(validAltitudes.Count / 2);
    double minimum = double.PositiveInfinity;
    double maximum = double.NegativeInfinity;
    for (int index = 0; index < vertices.Length; index++) {
      double altitude = rawAltitudes[index] ?? replacement;
      vertices[index] = vertices[index] with { AltitudeM = altitude };
      minimum = Math.Min(minimum, altitude);
      maximum = Math.Max(maximum, altitude);
    }

    return new Terrain3DWorld(
        center, normalized.RangeM, size, vertices, minimum, maximum,
        replacement, missing, texture: null);
  }

  internal static async Task<Terrain3DWorld> BuildAsync(
      Terrain3DSnapshot snapshot,
      Terrain3DSettings settings,
      string mapType,
      Func<double, double, double?> elevation,
      Func<string, TileInfo, CancellationToken, Task<byte[]?>> tileLoader,
      CancellationToken cancellationToken) {
    Terrain3DSettings normalized = settings.Normalize();
    Terrain3DWorld mesh = await Task.Run(
        () => BuildMesh(snapshot, normalized, elevation, cancellationToken),
        cancellationToken).ConfigureAwait(false);
    if (!normalized.ImageryEnabled) {
      return mesh;
    }

    try {
      Terrain3DTexture? texture = await LoadTextureAsync(
          mesh.Center, mesh.RangeM,
          normalized.TextureMinZoom, normalized.TextureMaxZoom, mapType,
          tileLoader, cancellationToken).ConfigureAwait(false);
      return new Terrain3DWorld(
          mesh.Center, mesh.RangeM, mesh.GridSize, mesh.Vertices,
          mesh.MinimumAltitudeM, mesh.MaximumAltitudeM, mesh.ReferenceAltitudeM,
          mesh.MissingSamples, texture);
    } finally {
      mesh.Dispose();
    }
  }

  internal static async Task<Terrain3DTexture?> LoadTextureAsync(
      Terrain3DGeoPoint center,
      double rangeM,
      int minimumZoom,
      int maximumZoom,
      string mapType,
      Func<string, TileInfo, CancellationToken, Task<byte[]?>> tileLoader,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(tileLoader);
    Terrain3DGeoPoint southWest = Terrain3DGeoMath.Offset(center, -rangeM, -rangeM, 0);
    Terrain3DGeoPoint northEast = Terrain3DGeoMath.Offset(center, rangeM, rangeM, 0);
    // A normal Web-Mercator extent cannot express a box crossing ±180 degrees. Falling back to
    // elevation shading is preferable to requesting an almost world-wide, visually incorrect atlas.
    if (Math.Abs(southWest.Longitude - northEast.Longitude) > 180) {
      return null;
    }
    (double minX, double minY) = SphericalMercator.FromLonLat(
        southWest.Longitude, southWest.Latitude);
    (double maxX, double maxY) = SphericalMercator.FromLonLat(
        northEast.Longitude, northEast.Latitude);
    var extent = new Extent(
        Math.Min(minX, maxX), Math.Min(minY, maxY),
        Math.Max(minX, maxX), Math.Max(minY, maxY));
    var schema = new GlobalSphericalMercator("png", minZoomLevel: 0, maxZoomLevel: 21);
    TileInfo[] tiles = [];
    minimumZoom = Math.Clamp(minimumZoom, 1, 20);
    int zoom = Math.Clamp(maximumZoom, minimumZoom, 20);
    for (; zoom >= minimumZoom; zoom--) {
      tiles = schema.GetTileInfos(extent, zoom).ToArray();
      if (tiles.Length <= MaximumTextureTiles) {
        break;
      }
    }
    if (tiles.Length == 0) {
      return null;
    }

    int minimumColumn = tiles.Min(tile => tile.Index.Col);
    int maximumColumn = tiles.Max(tile => tile.Index.Col);
    int minimumRow = tiles.Min(tile => tile.Index.Row);
    int maximumRow = tiles.Max(tile => tile.Index.Row);
    int columns = maximumColumn - minimumColumn + 1;
    int rows = maximumRow - minimumRow + 1;
    if (columns <= 0 || rows <= 0 || columns * rows > MaximumTextureTiles) {
      return null;
    }

    using var concurrency = new SemaphoreSlim(6, 6);
    Task<(TileInfo Tile, byte[]? Data)>[] loads = tiles.Select(async tile => {
      await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
      try {
        try {
          return (tile, await tileLoader(mapType, tile, cancellationToken).ConfigureAwait(false));
        } catch when (!cancellationToken.IsCancellationRequested) {
          return (tile, null);
        }
      } finally {
        concurrency.Release();
      }
    }).ToArray();
    (TileInfo Tile, byte[]? Data)[] loaded = await Task.WhenAll(loads).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();

    var atlas = new SKBitmap(
        columns * 256, rows * 256, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(atlas);
    canvas.Clear(new SKColor(52, 58, 55));
    using var missingPaint = new SKPaint {
      Color = new SKColor(67, 76, 71),
      Style = SKPaintStyle.Stroke,
      StrokeWidth = 1,
    };
    for (int column = 0; column <= columns; column++) {
      canvas.DrawLine(column * 256, 0, column * 256, rows * 256, missingPaint);
    }
    for (int row = 0; row <= rows; row++) {
      canvas.DrawLine(0, row * 256, columns * 256, row * 256, missingPaint);
    }

    int good = 0;
    foreach ((TileInfo tile, byte[]? data) in loaded) {
      cancellationToken.ThrowIfCancellationRequested();
      if (data is not { Length: > 0 }) {
        continue;
      }
      using SKBitmap? image = SKBitmap.Decode(data);
      if (image == null || image.Width <= 0 || image.Height <= 0) {
        continue;
      }
      int left = (tile.Index.Col - minimumColumn) * 256;
      int top = (tile.Index.Row - minimumRow) * 256;
      canvas.DrawBitmap(image, new SKRect(left, top, left + 256, top + 256));
      good++;
    }
    canvas.Flush();
    if (good == 0) {
      atlas.Dispose();
      return null;
    }
    return new Terrain3DTexture(
        atlas, zoom, minimumColumn, minimumRow, columns, rows, good, tiles.Length);
  }
}

internal readonly record struct Terrain3DProjectedPoint(
    float X,
    float Y,
    float Depth,
    bool Visible);

internal static class Terrain3DProjection {
  internal const double FieldOfViewDeg = 90;
  internal const double NearPlaneM = 2;

  internal static Terrain3DProjectedPoint Project(
      Terrain3DCamera camera,
      Vector3 world,
      int width,
      int height) {
    (Vector3 forward, Vector3 right, Vector3 up) = Basis(camera);
    var origin = new Vector3(
        (float)camera.EastM, (float)camera.NorthM, (float)camera.AltitudeM);
    Vector3 delta = world - origin;
    float viewX = Vector3.Dot(delta, right);
    float viewY = Vector3.Dot(delta, up);
    float depth = Vector3.Dot(delta, forward);
    if (!float.IsFinite(depth) || depth <= NearPlaneM || width <= 0 || height <= 0) {
      return new Terrain3DProjectedPoint(0, 0, depth, false);
    }
    double focal = height / (2 * Math.Tan(FieldOfViewDeg * Math.PI / 360));
    float x = (float)(width / 2d + viewX / depth * focal);
    float y = (float)(height / 2d - viewY / depth * focal);
    bool visible = float.IsFinite(x) && float.IsFinite(y)
        && x >= -width * 2 && x <= width * 3
        && y >= -height * 2 && y <= height * 3;
    return new Terrain3DProjectedPoint(x, y, depth, visible);
  }

  internal static Vector3 ScreenRay(
      Terrain3DCamera camera,
      double x,
      double y,
      int width,
      int height) {
    if (width <= 0 || height <= 0) {
      throw new ArgumentOutOfRangeException(nameof(width));
    }
    (Vector3 forward, Vector3 right, Vector3 up) = Basis(camera);
    double tangent = Math.Tan(FieldOfViewDeg * Math.PI / 360);
    double normalizedX = (x / width * 2 - 1) * (width / (double)height) * tangent;
    double normalizedY = (1 - y / height * 2) * tangent;
    return Vector3.Normalize(
        forward + right * (float)normalizedX + up * (float)normalizedY);
  }

  internal static (Vector3 Forward, Vector3 Right, Vector3 Up) Basis(
      Terrain3DCamera camera) {
    double yaw = camera.YawDeg * Math.PI / 180;
    double pitch = camera.PitchDeg * Math.PI / 180;
    double roll = camera.RollDeg * Math.PI / 180;
    var forward = Vector3.Normalize(new Vector3(
        (float)(Math.Sin(yaw) * Math.Cos(pitch)),
        (float)(Math.Cos(yaw) * Math.Cos(pitch)),
        (float)Math.Sin(pitch)));
    var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
    if (!IsFinite(right) || right.LengthSquared() < 0.1f) {
      right = Vector3.UnitX;
    }
    var up = Vector3.Normalize(Vector3.Cross(right, forward));
    Vector3 rolledRight = right * (float)Math.Cos(roll) + up * (float)Math.Sin(roll);
    Vector3 rolledUp = up * (float)Math.Cos(roll) - right * (float)Math.Sin(roll);
    return (forward, Vector3.Normalize(rolledRight), Vector3.Normalize(rolledUp));
  }

  private static bool IsFinite(Vector3 value) =>
      float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

internal readonly record struct Terrain3DRenderResult(
    int Width,
    int Height,
    int RowBytes,
    byte[] Pixels,
    string Details);

internal static class Terrain3DRenderer {
  private readonly record struct Triangle(
      int A,
      int B,
      int C,
      float Depth,
      float Brightness);

  internal static Terrain3DRenderResult Render(
      Terrain3DWorld world,
      Terrain3DSnapshot snapshot,
      Terrain3DSettings settings,
      Terrain3DCamera camera,
      int requestedWidth,
      int requestedHeight) {
    Terrain3DSettings normalized = settings.Normalize();
    int width = Math.Clamp(requestedWidth, 320, 1280);
    int height = Math.Clamp(requestedHeight, 240, 800);
    using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap);
    DrawBackground(canvas, width, height, camera.RollDeg);

    Terrain3DVertex[] source = world.Vertices;
    var projected = new Terrain3DProjectedPoint[source.Length];
    for (int index = 0; index < source.Length; index++) {
      Terrain3DVertex vertex = source[index];
      double altitude = world.ReferenceAltitudeM
          + (vertex.AltitudeM - world.ReferenceAltitudeM) * normalized.VerticalExaggeration;
      projected[index] = Terrain3DProjection.Project(camera,
          new Vector3((float)vertex.EastM, (float)vertex.NorthM, (float)altitude),
          width, height);
    }

    List<Triangle> triangles = BuildTriangles(world, projected);
    DrawTerrain(canvas, world, projected, triangles, normalized);
    DrawWaypoints(canvas, world, snapshot, normalized, camera, width, height);
    DrawOverlay(canvas, world, snapshot, camera, width, height);
    canvas.Flush();

    int stride = bitmap.RowBytes;
    var pixels = new byte[stride * height];
    IntPtr address = bitmap.GetPixels();
    for (int row = 0; row < height; row++) {
      Marshal.Copy(IntPtr.Add(address, row * stride), pixels, row * stride, stride);
    }
    string texture = world.Texture == null
        ? "elevation shading"
        : $"imagery z{world.Texture.Zoom} {world.Texture.LoadedTiles}/{world.Texture.RequestedTiles}";
    return new Terrain3DRenderResult(
        width, height, stride, pixels,
        $"{world.GridSize}×{world.GridSize} mesh · {texture} · "
        + $"DEM missing {world.MissingSamples}/{world.Vertices.Length}");
  }

  private static List<Triangle> BuildTriangles(
      Terrain3DWorld world,
      Terrain3DProjectedPoint[] projected) {
    var result = new List<Triangle>((world.GridSize - 1) * (world.GridSize - 1) * 2);
    for (int row = 0; row < world.GridSize - 1; row++) {
      for (int column = 0; column < world.GridSize - 1; column++) {
        int a = row * world.GridSize + column;
        int b = a + 1;
        int c = a + world.GridSize;
        int d = c + 1;
        Add(a, c, d);
        Add(a, d, b);
      }
    }
    result.Sort((left, right) => right.Depth.CompareTo(left.Depth));
    return result;

    void Add(int a, int b, int c) {
      if (!projected[a].Visible || !projected[b].Visible || !projected[c].Visible) {
        return;
      }
      Terrain3DVertex va = world.Vertices[a];
      Terrain3DVertex vb = world.Vertices[b];
      Terrain3DVertex vc = world.Vertices[c];
      var edge1 = new Vector3(
          (float)(vb.EastM - va.EastM),
          (float)(vb.NorthM - va.NorthM),
          (float)(vb.AltitudeM - va.AltitudeM));
      var edge2 = new Vector3(
          (float)(vc.EastM - va.EastM),
          (float)(vc.NorthM - va.NorthM),
          (float)(vc.AltitudeM - va.AltitudeM));
      Vector3 normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
      if (normal.Z < 0) {
        normal = -normal;
      }
      float light = Math.Clamp(Vector3.Dot(normal,
          Vector3.Normalize(new Vector3(-0.35f, -0.25f, 1))), 0, 1);
      result.Add(new Triangle(a, b, c,
          (projected[a].Depth + projected[b].Depth + projected[c].Depth) / 3,
          0.58f + light * 0.42f));
    }
  }

  private static void DrawTerrain(
      SKCanvas canvas,
      Terrain3DWorld world,
      Terrain3DProjectedPoint[] projected,
      IReadOnlyList<Triangle> triangles,
      Terrain3DSettings settings) {
    if (triangles.Count == 0) {
      return;
    }
    var points = new SKPoint[triangles.Count * 3];
    var colors = new SKColor[points.Length];
    SKPoint[]? textureCoordinates = world.Texture == null ? null : new SKPoint[points.Length];
    int output = 0;
    foreach (Triangle triangle in triangles) {
      foreach (int index in new[] { triangle.A, triangle.B, triangle.C }) {
        Terrain3DProjectedPoint point = projected[index];
        points[output] = new SKPoint(point.X, point.Y);
        float fog = settings.FogEnabled
            ? (float)Math.Clamp(
                (point.Depth - world.RangeM * 0.25) / (world.RangeM * 1.8), 0, 0.82)
            : 0;
        if (world.Texture != null) {
          byte shade = (byte)Math.Clamp(triangle.Brightness * 255 + fog * 30, 0, 255);
          colors[output] = new SKColor(shade, shade, shade, (byte)(255 - fog * 95));
          Terrain3DVertex vertex = world.Vertices[index];
          textureCoordinates![output] = world.Texture.Coordinate(
              vertex.Latitude, vertex.Longitude);
        } else {
          Terrain3DVertex vertex = world.Vertices[index];
          colors[output] = ElevationColor(
              vertex.AltitudeM, world.MinimumAltitudeM, world.MaximumAltitudeM,
              triangle.Brightness, fog);
        }
        output++;
      }
    }

    using SKVertices vertices = SKVertices.CreateCopy(
        SKVertexMode.Triangles, points, textureCoordinates, colors);
    using var paint = new SKPaint { IsAntialias = true };
    if (world.Texture != null) {
      paint.Shader = world.Texture.Bitmap.ToShader(
          SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
    }
    canvas.DrawVertices(vertices, SKBlendMode.Modulate, paint);

    using var gridPaint = new SKPaint {
      Color = new SKColor(255, 255, 255, 28),
      IsAntialias = true,
      Style = SKPaintStyle.Stroke,
      StrokeWidth = 1,
    };
    int step = Math.Max(2, world.GridSize / 8);
    for (int row = 0; row < world.GridSize; row += step) {
      DrawGridLine(Enumerable.Range(0, world.GridSize).Select(column => row * world.GridSize + column));
    }
    for (int column = 0; column < world.GridSize; column += step) {
      DrawGridLine(Enumerable.Range(0, world.GridSize).Select(row => row * world.GridSize + column));
    }

    void DrawGridLine(IEnumerable<int> indices) {
      using var path = new SKPath();
      bool open = false;
      foreach (int index in indices) {
        Terrain3DProjectedPoint point = projected[index];
        if (!point.Visible) {
          open = false;
          continue;
        }
        if (!open) {
          path.MoveTo(point.X, point.Y);
          open = true;
        } else {
          path.LineTo(point.X, point.Y);
        }
      }
      canvas.DrawPath(path, gridPaint);
    }
  }

  private static void DrawWaypoints(
      SKCanvas canvas,
      Terrain3DWorld world,
      Terrain3DSnapshot snapshot,
      Terrain3DSettings settings,
      Terrain3DCamera camera,
      int width,
      int height) {
    Terrain3DWaypoint[] mission = snapshot.Waypoints
        .Where(waypoint => waypoint.Kind == Terrain3DWaypointKind.Mission)
        .OrderBy(waypoint => waypoint.Sequence).ToArray();
    using var routePaint = new SKPaint {
      Color = SKColors.Red,
      Style = SKPaintStyle.Stroke,
      StrokeWidth = 3,
      IsAntialias = true,
    };
    using var route = new SKPath();
    bool pathOpen = false;
    foreach (Terrain3DWaypoint waypoint in mission) {
      Terrain3DProjectedPoint point = ProjectWaypoint(waypoint.Position);
      if (!point.Visible) {
        pathOpen = false;
        continue;
      }
      if (!pathOpen) {
        route.MoveTo(point.X, point.Y);
        pathOpen = true;
      } else {
        route.LineTo(point.X, point.Y);
      }
    }
    canvas.DrawPath(route, routePaint);

    using var markerPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
    using var outlinePaint = new SKPaint {
      Color = SKColors.Black,
      Style = SKPaintStyle.Stroke,
      StrokeWidth = 2,
      IsAntialias = true,
    };
    using var textPaint = new SKPaint {
      Color = SKColors.White,
      IsAntialias = true,
    };
    using var markerTypeface = SKTypeface.FromFamilyName("Inter", SKFontStyle.Bold);
    using var markerFont = new SKFont(markerTypeface, 13);
    foreach (Terrain3DWaypoint waypoint in snapshot.Waypoints) {
      Terrain3DProjectedPoint point = ProjectWaypoint(waypoint.Position);
      if (!point.Visible || point.X < -30 || point.X > width + 30
          || point.Y < -30 || point.Y > height + 30) {
        continue;
      }
      markerPaint.Color = waypoint.Kind switch {
        Terrain3DWaypointKind.Guided => SKColors.Gold,
        Terrain3DWaypointKind.Target => SKColors.DeepSkyBlue,
        Terrain3DWaypointKind.Vehicle => SKColors.LimeGreen,
        _ => SKColors.Red,
      };
      float radius = waypoint.Kind == Terrain3DWaypointKind.Mission ? 6 : 8;
      canvas.DrawCircle(point.X, point.Y, radius, markerPaint);
      canvas.DrawCircle(point.X, point.Y, radius, outlinePaint);
      canvas.DrawText(
          waypoint.Label, point.X + radius + 3, point.Y - radius,
          SKTextAlign.Left, markerFont, textPaint);
    }
    return;

    Terrain3DProjectedPoint ProjectWaypoint(Terrain3DGeoPoint position) {
      (double east, double north) = Terrain3DGeoMath.ToLocal(world.Center, position);
      double altitude = world.ReferenceAltitudeM
          + (position.AltitudeM - world.ReferenceAltitudeM) * settings.VerticalExaggeration;
      return Terrain3DProjection.Project(camera,
          new Vector3((float)east, (float)north, (float)altitude), width, height);
    }
  }

  private static void DrawBackground(SKCanvas canvas, int width, int height, double rollDeg) {
    using var skyPaint = new SKPaint {
      Shader = SKShader.CreateLinearGradient(
          new SKPoint(0, 0), new SKPoint(0, height),
          [new SKColor(47, 104, 164), new SKColor(157, 196, 221), new SKColor(203, 214, 198)],
          [0, 0.65f, 1], SKShaderTileMode.Clamp),
    };
    canvas.DrawRect(0, 0, width, height, skyPaint);
    using var horizon = new SKPaint {
      Color = new SKColor(255, 255, 255, 70),
      StrokeWidth = 1,
      IsAntialias = true,
    };
    double radians = rollDeg * Math.PI / 180;
    float half = width;
    canvas.DrawLine(
        (float)(width / 2 - Math.Cos(radians) * half),
        (float)(height / 2 - Math.Sin(radians) * half),
        (float)(width / 2 + Math.Cos(radians) * half),
        (float)(height / 2 + Math.Sin(radians) * half), horizon);
  }

  private static void DrawOverlay(
      SKCanvas canvas,
      Terrain3DWorld world,
      Terrain3DSnapshot snapshot,
      Terrain3DCamera camera,
      int width,
      int height) {
    using var panel = new SKPaint { Color = new SKColor(0, 0, 0, 135) };
    canvas.DrawRoundRect(new SKRect(12, 12, 345, 92), 6, 6, panel);
    using var text = new SKPaint {
      Color = SKColors.White,
      IsAntialias = true,
    };
    using var overlayTypeface = SKTypeface.FromFamilyName("Inter");
    using var overlayFont = new SKFont(overlayTypeface, 14);
    canvas.DrawText(
        $"SYS {snapshot.SystemId}:{snapshot.ComponentId}  {snapshot.Mode}  "
        + (snapshot.Armed ? "ARMED" : "DISARMED"),
        22, 35, SKTextAlign.Left, overlayFont, text);
    canvas.DrawText(
        $"{snapshot.Vehicle.Latitude:0.000000}, {snapshot.Vehicle.Longitude:0.000000}  "
        + $"AMSL {snapshot.Vehicle.AltitudeM:0.0} m",
        22, 57, SKTextAlign.Left, overlayFont, text);
    canvas.DrawText(
        $"HDG {camera.YawDeg:000}°  P {camera.PitchDeg:+0.0;-0.0;0}°  "
        + $"R {camera.RollDeg:+0.0;-0.0;0}°  terrain {world.MinimumAltitudeM:0}…{world.MaximumAltitudeM:0} m",
        22, 79, SKTextAlign.Left, overlayFont, text);

    using var reticle = new SKPaint {
      Color = new SKColor(255, 255, 255, 185),
      StrokeWidth = 1.5f,
      IsAntialias = true,
    };
    canvas.DrawLine(width / 2f - 10, height / 2f, width / 2f + 10, height / 2f, reticle);
    canvas.DrawLine(width / 2f, height / 2f - 10, width / 2f, height / 2f + 10, reticle);
  }

  private static SKColor ElevationColor(
      double altitude,
      double minimum,
      double maximum,
      float brightness,
      float fog) {
    double ratio = maximum <= minimum ? 0.5 : Math.Clamp((altitude - minimum) / (maximum - minimum), 0, 1);
    SKColor low = new(48, 105, 54);
    SKColor middle = new(122, 112, 69);
    SKColor high = new(196, 196, 185);
    SKColor baseColor = ratio < 0.55
        ? Blend(low, middle, ratio / 0.55)
        : Blend(middle, high, (ratio - 0.55) / 0.45);
    byte Shade(byte value) => (byte)Math.Clamp(value * brightness + fog * 60, 0, 255);
    return new SKColor(
        Shade(baseColor.Red), Shade(baseColor.Green), Shade(baseColor.Blue),
        (byte)(255 - fog * 70));
  }

  private static SKColor Blend(SKColor from, SKColor to, double amount) => new(
      (byte)(from.Red + (to.Red - from.Red) * amount),
      (byte)(from.Green + (to.Green - from.Green) * amount),
      (byte)(from.Blue + (to.Blue - from.Blue) * amount));
}

internal static class Terrain3DGeoMath {
  private const double EarthRadiusM = 6378137;

  internal static bool IsValid(Terrain3DGeoPoint point) =>
      double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude)
      && double.IsFinite(point.AltitudeM)
      && point.Latitude is >= -90 and <= 90
      && point.Longitude is >= -180 and <= 180
      && (Math.Abs(point.Latitude) > 1e-9 || Math.Abs(point.Longitude) > 1e-9);

  internal static Terrain3DGeoPoint Offset(
      Terrain3DGeoPoint origin,
      double eastM,
      double northM,
      double altitudeDeltaM) {
    double latitude = origin.Latitude + northM / EarthRadiusM * 180 / Math.PI;
    double cos = Math.Max(1e-6, Math.Cos(origin.Latitude * Math.PI / 180));
    double longitude = origin.Longitude + eastM / (EarthRadiusM * cos) * 180 / Math.PI;
    longitude = ((longitude + 540) % 360) - 180;
    return new Terrain3DGeoPoint(latitude, longitude, origin.AltitudeM + altitudeDeltaM);
  }

  internal static (double EastM, double NorthM) ToLocal(
      Terrain3DGeoPoint origin,
      Terrain3DGeoPoint point) {
    double north = (point.Latitude - origin.Latitude) * Math.PI / 180 * EarthRadiusM;
    double meanLatitude = (point.Latitude + origin.Latitude) / 2 * Math.PI / 180;
    double east = (point.Longitude - origin.Longitude) * Math.PI / 180
        * EarthRadiusM * Math.Cos(meanLatitude);
    if (east > Math.PI * EarthRadiusM) {
      east -= 2 * Math.PI * EarthRadiusM;
    } else if (east < -Math.PI * EarthRadiusM) {
      east += 2 * Math.PI * EarthRadiusM;
    }
    return (east, north);
  }

  internal static (double PixelX, double PixelY) WebMercatorPixel(
      double latitude,
      double longitude,
      int zoom) {
    latitude = Math.Clamp(latitude, -85.05112878, 85.05112878);
    double scale = Math.Pow(2, zoom) * 256;
    double x = (longitude + 180) / 360 * scale;
    double radians = latitude * Math.PI / 180;
    double y = (1 - Math.Log(Math.Tan(radians) + 1 / Math.Cos(radians)) / Math.PI) / 2 * scale;
    return (x, y);
  }
}

internal static class Terrain3DRaycaster {
  internal static bool TryIntersect(
      Terrain3DWorld world,
      Terrain3DCamera camera,
      Vector3 ray,
      out Terrain3DGeoPoint hit) {
    hit = default;
    if (!float.IsFinite(ray.X) || !float.IsFinite(ray.Y) || !float.IsFinite(ray.Z)
        || ray.LengthSquared() < 0.5f) {
      return false;
    }
    ray = Vector3.Normalize(ray);
    double step = Math.Max(2, world.RangeM / (world.GridSize - 1));
    double maximumDistance = world.RangeM * 3;
    double previousDistance = Terrain3DProjection.NearPlaneM;
    double previousClearance = Clearance(previousDistance, out _, out _, out _);
    bool enteredWorld = false;
    for (double distance = previousDistance + step;
         distance <= maximumDistance; distance += step) {
      double clearance = Clearance(distance, out double east, out double north, out double terrain);
      bool inside = Math.Abs(east) <= world.RangeM && Math.Abs(north) <= world.RangeM;
      if (!inside) {
        if (enteredWorld) {
          break;
        }
        previousDistance = distance;
        previousClearance = clearance;
        continue;
      }
      enteredWorld = true;
      if (!double.IsFinite(clearance)) {
        previousDistance = distance;
        previousClearance = clearance;
        continue;
      }
      if (clearance <= 0 && double.IsFinite(previousClearance) && previousClearance > 0) {
        double fraction = previousClearance / (previousClearance - clearance);
        double resolvedDistance = previousDistance + (distance - previousDistance) * fraction;
        _ = Clearance(resolvedDistance, out east, out north, out terrain);
        Terrain3DGeoPoint location = Terrain3DGeoMath.Offset(
            world.Center, east, north, terrain - world.Center.AltitudeM);
        hit = location with { AltitudeM = terrain };
        return Terrain3DGeoMath.IsValid(hit);
      }
      previousDistance = distance;
      previousClearance = clearance;
    }
    return false;

    double Clearance(
        double distance,
        out double east,
        out double north,
        out double terrain) {
      east = camera.EastM + ray.X * distance;
      north = camera.NorthM + ray.Y * distance;
      terrain = world.SampleAltitude(east, north);
      double altitude = camera.AltitudeM + ray.Z * distance;
      return double.IsFinite(terrain) ? altitude - terrain : double.NaN;
    }
  }
}

internal static class Terrain3DSnapshotFactory {
  internal static Terrain3DSnapshot Capture(
      MissionPlanner.MAVLinkInterface comPort,
      DateTime capturedUtc,
      Func<double, double, double?> elevation) {
    ArgumentNullException.ThrowIfNull(comPort);
    ArgumentNullException.ThrowIfNull(elevation);
    MissionPlanner.CurrentState state = comPort.MAV.cs;
    double multiplier = MissionPlanner.CurrentState.multiplieralt;
    if (!double.IsFinite(multiplier) || multiplier <= 0) {
      multiplier = 1;
    }
    double relativeAltitude = state.alt / multiplier;
    double vehicleAltitude = state.altasl / multiplier;
    double homeAltitude = state.HomeAlt;
    if (!double.IsFinite(homeAltitude)) {
      homeAltitude = vehicleAltitude - relativeAltitude;
    }
    if ((!double.IsFinite(vehicleAltitude) || Math.Abs(vehicleAltitude) < 1e-6)
        && double.IsFinite(homeAltitude) && double.IsFinite(relativeAltitude)) {
      vehicleAltitude = homeAltitude + relativeAltitude;
    }

    var vehicle = new Terrain3DGeoPoint(state.lat, state.lng, vehicleAltitude);
    var waypoints = new List<Terrain3DWaypoint>();
    foreach (KeyValuePair<int, MAVLink.mavlink_mission_item_int_t> item in
             comPort.MAV.wps.OrderBy(item => item.Key)) {
      MAVLink.mavlink_mission_item_int_t mission = item.Value;
      if (!MissionRoute.IsNavigation(mission.command)
          || !MissionPlannerAvalonia.Controls.MapView.TryGlobalPosition(
              mission, out double latitude, out double longitude)) {
        continue;
      }
      double altitude = ResolveAltitude(
          mission.frame, mission.z, latitude, longitude, homeAltitude, elevation);
      var point = new Terrain3DGeoPoint(latitude, longitude, altitude);
      if (Terrain3DGeoMath.IsValid(point)) {
        waypoints.Add(new Terrain3DWaypoint(
            item.Key, point, item.Key == 0 ? "HOME" : $"WP {item.Key}"));
      }
    }

    MAVLink.mavlink_mission_item_int_t guided = comPort.MAV.GuidedMode;
    if (MissionPlannerAvalonia.Controls.MapView.TryGlobalPosition(
            guided, out double guidedLatitude, out double guidedLongitude)) {
      double altitude = ResolveAltitude(
          guided.frame, guided.z, guidedLatitude, guidedLongitude, homeAltitude, elevation);
      var point = new Terrain3DGeoPoint(guidedLatitude, guidedLongitude, altitude);
      if (Terrain3DGeoMath.IsValid(point)) {
        waypoints.Add(new Terrain3DWaypoint(
            int.MaxValue - 2, point, "GUIDED", Terrain3DWaypointKind.Guided));
      }
    }

    MissionPlanner.Utilities.PointLatLngAlt target = state.TargetLocation;
    var targetPoint = new Terrain3DGeoPoint(target.Lat, target.Lng, target.Alt);
    if (Terrain3DGeoMath.IsValid(targetPoint)) {
      waypoints.Add(new Terrain3DWaypoint(
          int.MaxValue - 1, targetPoint, "TARGET", Terrain3DWaypointKind.Target));
    }
    if (Terrain3DGeoMath.IsValid(vehicle)) {
      waypoints.Add(new Terrain3DWaypoint(
          int.MaxValue, vehicle, "MAV", Terrain3DWaypointKind.Vehicle));
    }

    return new Terrain3DSnapshot(
        vehicle,
        relativeAltitude,
        state.roll,
        state.pitch,
        state.yaw,
        state.vx,
        state.vy,
        -state.vz,
        capturedUtc,
        state.mode ?? "",
        state.armed,
        comPort.MAV.sysid,
        comPort.MAV.compid,
        waypoints);
  }

  internal static double ResolveAltitude(
      byte frame,
      double altitude,
      double latitude,
      double longitude,
      double homeAltitude,
      Func<double, double, double?> elevation) {
    // MAV_FRAME's deprecated *_INT aliases retain wire values 5, 6 and 11. Mission items may
    // still contain them even though MISSION_ITEM_INT no longer requires a distinct frame name.
    if (frame is (byte)MAVLink.MAV_FRAME.GLOBAL or 5) {
      return altitude;
    }
    if (frame is (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT or 11) {
      return (elevation(latitude, longitude) ?? homeAltitude) + altitude;
    }
    return homeAltitude + altitude;
  }
}
