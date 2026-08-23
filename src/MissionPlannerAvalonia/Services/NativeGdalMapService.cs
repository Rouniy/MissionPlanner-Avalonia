using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using BruTile.Web;
using Mapsui.Projections;
using MissionPlanner.Utilities;
using SkiaSharp;

namespace MissionPlannerAvalonia.Services;

internal readonly record struct NativeGdalScanProgress(
    int Completed,
    int Total,
    string CurrentFile);

public sealed record NativeGdalRasterFile(
    string FullPath,
    string Driver,
    bool Indexed,
    string Size,
    string Coverage,
    string? Error = null) {
  public string Name => Path.GetFileName(FullPath);
  public string State => Error != null ? "Error" : Indexed ? "Indexed" : "Skipped";
}

internal sealed record NativeGdalScanResult(
    string Directory,
    string Backend,
    IReadOnlyList<NativeGdalRasterFile> Files,
    int ExaminedFiles,
    int UnrecognizedFiles,
    DateTime CompletedUtc) {
  internal int IndexedCount => Files.Count(file => file.Indexed);
  internal int ErrorCount => Files.Count(file => file.Error != null);
}

/// <summary>
/// Ports official Mission Planner's GDAL Custom raster-map provider to Mapsui. GDAL is loaded only
/// when installed by the operator; the ordinary managed GeoTIFF/DTED elevation path stays active
/// when it is absent. Datasets are opened read-only, warped into EPSG:3857 VRTs and sampled directly
/// into requested map tiles instead of loading full-resolution images into process memory.
/// </summary>
internal static class NativeGdalMapService {
  internal const string MapType = "GDAL Custom";
  internal const string SettingsKey = ElevationSourceService.SettingsKey;

  private static readonly object _stateGate = new();
  private static readonly SemaphoreSlim _scanGate = new(1, 1);
  private static readonly SemaphoreSlim _renderGate = new(2, 2);
  private static readonly Lazy<NativeGdalLoadResult> _backend =
      new(NativeGdalApi.TryLoad, LazyThreadSafetyMode.ExecutionAndPublication);
  private static NativeGdalDataset[] _datasets = [];
  private static NativeGdalScanResult? _lastResult;
  private static Task<NativeGdalScanResult>? _startupTask;
  private static bool _startupInitialized;
  private static bool _shuttingDown;

  internal static bool IsAvailable => _backend.Value.Api != null;
  internal static string BackendStatus => _backend.Value.Api is { } api
      ? $"GDAL {api.Version} via {api.LibraryPath}"
      : _backend.Value.Status;
  internal static string SavedDirectory => Settings.Instance.GetString(SettingsKey, "").Trim();
  internal static bool HasConfiguration =>
      !string.IsNullOrWhiteSpace(SavedDirectory)
      && Directory.Exists(SavedDirectory)
      && IsAvailable;

  internal static NativeGdalScanResult? LastResult {
    get {
      lock (_stateGate) {
        return _lastResult;
      }
    }
  }

  internal static Task<NativeGdalScanResult>? StartupTask {
    get {
      lock (_stateGate) {
        return _startupTask;
      }
    }
  }

  internal static void InitializeFromSettings() {
    lock (_stateGate) {
      if (_startupInitialized) {
        return;
      }
      _startupInitialized = true;
      string directory = SavedDirectory;
      if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) {
        return;
      }
      _startupTask = ScanAsync(directory, progress: null, CancellationToken.None);
      _ = ObserveStartupAsync(_startupTask);
    }
  }

  internal static Task<NativeGdalScanResult> ScanAsync(
      string directory,
      IProgress<NativeGdalScanProgress>? progress,
      CancellationToken cancellationToken) => Task.Run(
          () => ScanCore(directory, progress, cancellationToken), cancellationToken);

  internal static ILocalTileSource CreateTileSource(
      HttpTileSource satelliteSource, HttpClient httpClient) =>
      new NativeGdalTileSource(satelliteSource, httpClient);

  internal static void Unload() {
    NativeGdalDataset[] previous;
    lock (_stateGate) {
      previous = _datasets;
      _datasets = [];
      _lastResult = null;
    }
    foreach (NativeGdalDataset dataset in previous) {
      dataset.Dispose();
    }
  }

  internal static void Shutdown() {
    lock (_stateGate) {
      _shuttingDown = true;
    }
    Unload();
  }

  internal static byte[]? RenderTile(Extent extent, int tileSize = 256) {
    if (tileSize is < 1 or > 2048
        || !double.IsFinite(extent.MinX)
        || !double.IsFinite(extent.MinY)
        || !double.IsFinite(extent.MaxX)
        || !double.IsFinite(extent.MaxY)
        || extent.Width <= 0
        || extent.Height <= 0) {
      return null;
    }

    NativeGdalDataset[] snapshot;
    lock (_stateGate) {
      snapshot = _datasets;
    }
    if (snapshot.Length == 0) {
      return null;
    }

    var rgba = new byte[checked(tileSize * tileSize * 4)];
    bool rendered = false;
    // Coarse files are registered first; finer rasters overwrite them where they overlap.
    foreach (NativeGdalDataset dataset in snapshot) {
      rendered |= dataset.Render(extent, tileSize, rgba);
    }
    return rendered ? EncodeRgba(rgba, tileSize) : null;
  }

  internal static byte[]? CompositeTiles(byte[]? baseTile, byte[]? localTile, int tileSize = 256) {
    if (localTile is not { Length: > 0 }) {
      return baseTile;
    }
    if (baseTile is not { Length: > 0 }) {
      return localTile;
    }

    using SKBitmap? background = SKBitmap.Decode(baseTile);
    using SKBitmap? overlay = SKBitmap.Decode(localTile);
    if (background == null) {
      return localTile;
    }
    if (overlay == null) {
      return baseTile;
    }
    using var surface = SKSurface.Create(new SKImageInfo(
        tileSize, tileSize, SKColorType.Rgba8888, SKAlphaType.Premul));
    if (surface == null) {
      return baseTile;
    }
    var destination = new SKRect(0, 0, tileSize, tileSize);
    surface.Canvas.Clear(SKColors.Transparent);
    surface.Canvas.DrawBitmap(background, destination);
    surface.Canvas.DrawBitmap(overlay, destination);
    surface.Canvas.Flush();
    using SKImage image = surface.Snapshot();
    using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
  }

  private static NativeGdalScanResult ScanCore(
      string directory,
      IProgress<NativeGdalScanProgress>? progress,
      CancellationToken cancellationToken) {
    _scanGate.Wait(cancellationToken);
    var loaded = new List<NativeGdalDataset>();
    try {
      string root = NormalizeExistingDirectory(directory);
      NativeGdalApi? api = _backend.Value.Api;
      if (api == null) {
        var unavailable = new NativeGdalScanResult(
            root, _backend.Value.Status, [], 0, 0, DateTime.UtcNow);
        Publish(unavailable, []);
        return unavailable;
      }

      IReadOnlyList<string> files = FindCandidates(root, cancellationToken);
      var visible = new List<NativeGdalRasterFile>();
      int unrecognized = 0;
      for (int index = 0; index < files.Count; index++) {
        cancellationToken.ThrowIfCancellationRequested();
        string file = files[index];
        progress?.Report(new NativeGdalScanProgress(index, files.Count, file));
        NativeGdalOpenResult result = NativeGdalDataset.TryOpen(api, file);
        if (result.Dataset != null) {
          loaded.Add(result.Dataset);
          visible.Add(result.File!);
        } else if (result.Recognized) {
          visible.Add(result.File!);
        } else {
          unrecognized++;
        }
      }
      cancellationToken.ThrowIfCancellationRequested();
      progress?.Report(new NativeGdalScanProgress(files.Count, files.Count, ""));

      loaded.Sort((left, right) => right.Resolution.CompareTo(left.Resolution));
      var scan = new NativeGdalScanResult(
          root,
          $"GDAL {api.Version} via {api.LibraryPath}",
          visible.OrderBy(file => file.FullPath, PathComparer).ToArray(),
          files.Count,
          unrecognized,
          DateTime.UtcNow);
      Publish(scan, loaded.ToArray());
      loaded.Clear();
      return scan;
    } finally {
      foreach (NativeGdalDataset dataset in loaded) {
        dataset.Dispose();
      }
      _scanGate.Release();
    }
  }

  private static IReadOnlyList<string> FindCandidates(
      string root, CancellationToken cancellationToken) {
    var options = new EnumerationOptions {
      RecurseSubdirectories = true,
      IgnoreInaccessible = true,
      ReturnSpecialDirectories = false,
      AttributesToSkip = FileAttributes.ReparsePoint,
    };
    var files = new List<string>();
    foreach (string path in Directory.EnumerateFiles(root, "*", options)) {
      cancellationToken.ThrowIfCancellationRequested();
      try {
        // Match official Mission Planner's guard against tiny sidecars and placeholder files.
        if (new FileInfo(path).Length >= 1024 && !IsKnownSidecar(path)) {
          files.Add(Path.GetFullPath(path));
        }
      } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        // A concurrently removed or unreadable file does not invalidate the rest of the scan.
      }
    }
    files.Sort(PathComparer);
    return files;
  }

  private static bool IsKnownSidecar(string path) {
    string name = Path.GetFileName(path);
    string extension = Path.GetExtension(path);
    return name.EndsWith(".aux.xml", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".prj", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".tfw", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jgw", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pgw", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".wld", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".ovr", StringComparison.OrdinalIgnoreCase);
  }

  private static void Publish(
      NativeGdalScanResult result, NativeGdalDataset[] replacement) {
    NativeGdalDataset[] previous;
    bool applied;
    lock (_stateGate) {
      previous = _datasets;
      applied = !_shuttingDown;
      if (applied) {
        _datasets = replacement;
        _lastResult = result;
      }
    }
    foreach (NativeGdalDataset dataset in applied ? previous : replacement) {
      dataset.Dispose();
    }
  }

  private static async Task ObserveStartupAsync(Task<NativeGdalScanResult> task) {
    try {
      await task;
      bool refresh;
      lock (_stateGate) {
        refresh = !_shuttingDown;
      }
      if (refresh && string.Equals(
              Settings.Instance["MapType"], MapType, StringComparison.Ordinal)) {
        MapTileSourceFactory.RefreshMapType(MapType);
      }
    } catch {
      // The setup page reports the error if opened; startup must remain non-blocking.
    }
  }

  private static string NormalizeExistingDirectory(string directory) {
    if (string.IsNullOrWhiteSpace(directory)) {
      throw new ArgumentException("Choose a local raster directory.", nameof(directory));
    }
    string fullPath = Path.GetFullPath(directory.Trim());
    if (!Directory.Exists(fullPath)) {
      throw new DirectoryNotFoundException(fullPath);
    }
    return fullPath;
  }

  private static byte[] EncodeRgba(byte[] rgba, int size) {
    using var bitmap = new SKBitmap(new SKImageInfo(
        size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    MarshalCopy(rgba, bitmap.GetPixels());
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
  }

  private static void MarshalCopy(byte[] source, nint destination) =>
      System.Runtime.InteropServices.Marshal.Copy(source, 0, destination, source.Length);

  private static StringComparer PathComparer =>
      OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
          ? StringComparer.OrdinalIgnoreCase
          : StringComparer.Ordinal;

  private sealed class NativeGdalTileSource : ILocalTileSource {
    private readonly HttpTileSource _satellite;
    private readonly HttpClient _httpClient;

    internal NativeGdalTileSource(HttpTileSource satellite, HttpClient httpClient) {
      _satellite = satellite;
      _httpClient = httpClient;
      Schema = satellite.Schema;
    }

    public ITileSchema Schema { get; }
    public string Name => MapType;
    public Attribution Attribution { get; } = new("© Google; local raster data via GDAL");

    public async Task<byte[]?> GetTileAsync(TileInfo tileInfo) {
      Task<byte[]?> baseTask = ReadBaseAsync(tileInfo);
      byte[]? local = null;
      await _renderGate.WaitAsync();
      try {
        local = await Task.Run(() => RenderTile(tileInfo.Extent));
      } finally {
        _renderGate.Release();
      }
      return CompositeTiles(await baseTask, local);
    }

    private async Task<byte[]?> ReadBaseAsync(TileInfo tileInfo) {
      try {
        return await _satellite.GetTileAsync(_httpClient, tileInfo, CancellationToken.None);
      } catch {
        // Local imagery remains usable offline even when the satellite base is unavailable.
        return null;
      }
    }
  }
}

internal sealed class NativeGdalDataset : IDisposable {
  private const int Gray = 1;
  private const int Palette = 2;
  private const int Red = 3;
  private const int Green = 4;
  private const int Blue = 5;
  private const int Alpha = 6;
  private const int AllValidMask = 0x01;

  private readonly object _gate = new();
  private readonly NativeGdalApi _api;
  private readonly nint _source;
  private readonly nint _warped;
  private readonly double[] _transform;
  private readonly int _width;
  private readonly int _height;
  private readonly int[] _interpretations;
  private bool _disposed;

  private NativeGdalDataset(
      NativeGdalApi api,
      nint source,
      nint warped,
      string path,
      string driver,
      int width,
      int height,
      int bands,
      double[] transform,
      Extent extent) {
    _api = api;
    _source = source;
    _warped = warped;
    Path = path;
    Driver = driver;
    _width = width;
    _height = height;
    Bands = bands;
    _transform = transform;
    Extent = extent;
    Resolution = Math.Max(Math.Abs(transform[1]), Math.Abs(transform[5]));
    _interpretations = Enumerable.Range(1, bands)
        .Select(index => api.ColorInterpretation(api.RasterBand(warped, index)))
        .ToArray();
  }

  internal string Path { get; }
  internal string Driver { get; }
  internal int Bands { get; }
  internal Extent Extent { get; }
  internal double Resolution { get; }

  internal static NativeGdalOpenResult TryOpen(NativeGdalApi api, string path) {
    nint source = api.OpenRaster(path);
    if (source == nint.Zero) {
      return new NativeGdalOpenResult(null, null, Recognized: false);
    }

    nint warped = nint.Zero;
    string driver = api.DriverName(source);
    try {
      if (api.RasterCount(source) <= 0) {
        return new NativeGdalOpenResult(null, null, Recognized: false);
      }
      warped = api.CreateWebMercatorVrt(source);
      if (warped == nint.Zero) {
        return Error(path, driver, "Cannot warp to EPSG:3857: " + api.LastError());
      }

      int width = api.RasterXSize(warped);
      int height = api.RasterYSize(warped);
      int bands = api.RasterCount(warped);
      var transform = new double[6];
      if (width <= 0 || height <= 0 || bands <= 0
          || api.GetGeoTransform(warped, transform) != 0
          || transform.Any(value => !double.IsFinite(value))
          || Math.Abs(transform[2]) > 1e-9
          || Math.Abs(transform[4]) > 1e-9
          || transform[1] <= 0
          || transform[5] >= 0) {
        return Error(path, driver, "Warped raster has no valid north-up geotransform.");
      }

      double x2 = transform[0] + width * transform[1];
      double y2 = transform[3] + height * transform[5];
      if (!double.IsFinite(x2) || !double.IsFinite(y2)) {
        return Error(path, driver, "Warped raster coverage is outside the finite map domain.");
      }
      var extent = new Extent(
          Math.Min(transform[0], x2), Math.Min(transform[3], y2),
          Math.Max(transform[0], x2), Math.Max(transform[3], y2));
      if (extent.Width <= 0 || extent.Height <= 0) {
        return Error(path, driver, "Warped raster coverage is empty.");
      }

      var dataset = new NativeGdalDataset(
          api, source, warped, path, driver, width, height, bands, transform, extent);
      source = nint.Zero;
      warped = nint.Zero;
      var (west, south) = SphericalMercator.ToLonLat(extent.MinX, extent.MinY);
      var (east, north) = SphericalMercator.ToLonLat(extent.MaxX, extent.MaxY);
      var file = new NativeGdalRasterFile(
          path,
          driver,
          Indexed: true,
          Size: $"{width:N0} × {height:N0}, {bands} band(s)",
          Coverage: $"{south:0.#####},{west:0.#####} → {north:0.#####},{east:0.#####}");
      return new NativeGdalOpenResult(dataset, file, Recognized: true);
    } catch (Exception ex) {
      return Error(path, driver, ex.Message);
    } finally {
      api.Close(warped);
      api.Close(source);
    }
  }

  internal bool Render(Extent request, int tileSize, byte[] destination) {
    lock (_gate) {
      if (_disposed || !TryIntersection(request, Extent, out Extent intersection)) {
        return false;
      }

      int destinationLeft = Math.Clamp((int)Math.Floor(
          (intersection.MinX - request.MinX) / request.Width * tileSize), 0, tileSize - 1);
      int destinationRight = Math.Clamp((int)Math.Ceiling(
          (intersection.MaxX - request.MinX) / request.Width * tileSize), 1, tileSize);
      int destinationTop = Math.Clamp((int)Math.Floor(
          (request.MaxY - intersection.MaxY) / request.Height * tileSize), 0, tileSize - 1);
      int destinationBottom = Math.Clamp((int)Math.Ceiling(
          (request.MaxY - intersection.MinY) / request.Height * tileSize), 1, tileSize);
      int outputWidth = destinationRight - destinationLeft;
      int outputHeight = destinationBottom - destinationTop;
      if (outputWidth <= 0 || outputHeight <= 0) {
        return false;
      }

      int xOffset = Math.Clamp((int)Math.Floor(
          (intersection.MinX - _transform[0]) / _transform[1]), 0, _width - 1);
      int xEnd = Math.Clamp((int)Math.Ceiling(
          (intersection.MaxX - _transform[0]) / _transform[1]), xOffset + 1, _width);
      int yOffset = Math.Clamp((int)Math.Floor(
          (_transform[3] - intersection.MaxY) / -_transform[5]), 0, _height - 1);
      int yEnd = Math.Clamp((int)Math.Ceiling(
          (_transform[3] - intersection.MinY) / -_transform[5]), yOffset + 1, _height);
      int count = outputWidth * outputHeight;

      int redBand = FindBand(Red);
      int greenBand = FindBand(Green);
      int blueBand = FindBand(Blue);
      int grayBand = FindBand(Gray);
      int paletteBand = FindBand(Palette);
      int alphaBand = FindBand(Alpha);
      int fallbackBand = redBand > 0 ? redBand : grayBand > 0 ? grayBand : paletteBand > 0
          ? paletteBand : 1;
      bool channelsUndefined = redBand == 0 && greenBand == 0 && blueBand == 0
          && grayBand == 0 && paletteBand == 0;

      byte[]? red = Read(redBand > 0 ? redBand : grayBand > 0 ? grayBand : fallbackBand);
      byte[]? green = Read(greenBand > 0 ? greenBand : channelsUndefined && Bands >= 3
          ? 2 : grayBand > 0 ? grayBand : fallbackBand);
      byte[]? blue = Read(blueBand > 0 ? blueBand : channelsUndefined && Bands >= 3
          ? 3 : grayBand > 0 ? grayBand : fallbackBand);
      byte[]? alpha = alphaBand > 0 ? Read(alphaBand) : null;
      if (red == null || green == null || blue == null || alphaBand > 0 && alpha == null) {
        return false;
      }

      nint firstBand = _api.RasterBand(_warped, fallbackBand);
      byte[]? mask = (_api.MaskFlags(firstBand) & AllValidMask) == 0
          ? ReadHandle(_api.MaskBand(firstBand))
          : null;
      nint colorTable = paletteBand > 0
          ? _api.ColorTable(_api.RasterBand(_warped, paletteBand))
          : nint.Zero;

      bool any = false;
      for (int row = 0; row < outputHeight; row++) {
        for (int column = 0; column < outputWidth; column++) {
          int input = row * outputWidth + column;
          byte sourceRed = red[input];
          byte sourceGreen = green[input];
          byte sourceBlue = blue[input];
          byte sourceAlpha = alpha?[input] ?? byte.MaxValue;
          if (paletteBand > 0 && colorTable != nint.Zero
              && _api.TryGetColor(colorTable, red[input], out NativeGdalColorEntry color)) {
            sourceRed = ClampColor(color.Red);
            sourceGreen = ClampColor(color.Green);
            sourceBlue = ClampColor(color.Blue);
            sourceAlpha = (byte)(sourceAlpha * ClampColor(color.Alpha) / 255);
          }
          if (mask != null) {
            sourceAlpha = (byte)(sourceAlpha * mask[input] / 255);
          }
          if (sourceAlpha == 0) {
            continue;
          }
          int output = ((destinationTop + row) * tileSize + destinationLeft + column) * 4;
          Blend(destination, output, sourceRed, sourceGreen, sourceBlue, sourceAlpha);
          any = true;
        }
      }
      return any;

      byte[]? Read(int bandIndex) => ReadHandle(_api.RasterBand(_warped, bandIndex));

      byte[]? ReadHandle(nint band) {
        if (band == nint.Zero) {
          return null;
        }
        var buffer = new byte[count];
        return _api.ReadByteBand(
            band, xOffset, yOffset, xEnd - xOffset, yEnd - yOffset,
            buffer, outputWidth, outputHeight)
            ? buffer
            : null;
      }
    }
  }

  public void Dispose() {
    lock (_gate) {
      if (_disposed) {
        return;
      }
      _disposed = true;
      // The warped VRT owns references to the source but both handles retain their normal nested
      // close order according to GDALAutoCreateWarpedVRT's C API contract.
      _api.Close(_warped);
      _api.Close(_source);
    }
  }

  private int FindBand(int interpretation) {
    int index = Array.IndexOf(_interpretations, interpretation);
    return index < 0 ? 0 : index + 1;
  }

  private static NativeGdalOpenResult Error(string path, string driver, string error) =>
      new(null, new NativeGdalRasterFile(
          path, driver, Indexed: false, Size: "—", Coverage: "—", Error: error), true);

  internal static bool TryIntersection(Extent left, Extent right, out Extent intersection) {
    double minX = Math.Max(left.MinX, right.MinX);
    double minY = Math.Max(left.MinY, right.MinY);
    double maxX = Math.Min(left.MaxX, right.MaxX);
    double maxY = Math.Min(left.MaxY, right.MaxY);
    if (minX >= maxX || minY >= maxY) {
      intersection = default;
      return false;
    }
    intersection = new Extent(minX, minY, maxX, maxY);
    return true;
  }

  internal static void Blend(
      byte[] destination, int offset, byte red, byte green, byte blue, byte alpha) {
    int destinationAlpha = destination[offset + 3];
    int outputAlpha = alpha + destinationAlpha * (255 - alpha) / 255;
    if (outputAlpha == 0) {
      return;
    }
    destination[offset] = (byte)((red * alpha
        + destination[offset] * destinationAlpha * (255 - alpha) / 255) / outputAlpha);
    destination[offset + 1] = (byte)((green * alpha
        + destination[offset + 1] * destinationAlpha * (255 - alpha) / 255) / outputAlpha);
    destination[offset + 2] = (byte)((blue * alpha
        + destination[offset + 2] * destinationAlpha * (255 - alpha) / 255) / outputAlpha);
    destination[offset + 3] = (byte)outputAlpha;
  }

  private static byte ClampColor(short value) => (byte)Math.Clamp((int)value, 0, 255);
}

internal sealed record NativeGdalOpenResult(
    NativeGdalDataset? Dataset,
    NativeGdalRasterFile? File,
    bool Recognized);
