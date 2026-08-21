using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using BruTile.Predefined;
using Mapsui.Projections;
using MissionPlanner.Utilities;
using SkiaSharp;

namespace MissionPlannerAvalonia.Services;

internal sealed record LogHome(double Latitude, double Longitude, double Altitude) {
  public override string ToString() =>
      $"{Latitude.ToString("0.000000", CultureInfo.InvariantCulture)}, " +
      $"{Longitude.ToString("0.000000", CultureInfo.InvariantCulture)}, " +
      $"{Altitude.ToString("0.0", CultureInfo.InvariantCulture)} m";
}

internal sealed record LogIndexEntry(
    string FullPath,
    string RootPath,
    long SizeBytes,
    DateTime SourceLastWriteUtc,
    DateTime Date,
    string Frame,
    int SystemId,
    TimeSpan Duration,
    LogHome? Home,
    TimeSpan TimeInAir,
    double DistanceMeters,
    int CameraMessages,
    byte[] ThumbnailJpeg,
    string? Error,
    LogFileStamp? PairedRlog = null) {
  public string Name => Path.GetFileName(FullPath);
  public string Directory => Path.GetDirectoryName(FullPath) ?? "";
}

internal sealed record LogIndexProgress(int Completed, int Total, string CurrentFile);

internal sealed record LogFileStamp(long SizeBytes, DateTime LastWriteUtc);

internal sealed record LogIndexDeleteResult(
    IReadOnlyList<string> DeletedLogs,
    IReadOnlyList<string> Errors);

internal interface ILogIndexService {
  Task<IReadOnlyList<LogIndexEntry>> ScanAsync(
      string root, IProgress<LogIndexProgress>? progress, CancellationToken cancellationToken);
  Task<LogIndexDeleteResult> DeleteAsync(
      string root, IReadOnlyList<LogIndexEntry> entries, CancellationToken cancellationToken);
}

internal interface ILogThumbnailRenderer {
  Task<byte[]> GetOrCreateAsync(
      string sourcePath, IReadOnlyList<(double Lat, double Lng)> track,
      CancellationToken cancellationToken);
}

/// <summary>
/// Portable implementation of Mission Planner's LogIndex and LogMap workflows.
/// Parsing errors are isolated per file, and thumbnail tile backgrounds only use the
/// existing cache; indexing never performs an unbounded map-provider download.
/// </summary>
internal sealed class LogIndexService : ILogIndexService {
  private readonly ILogThumbnailRenderer _thumbnailRenderer;

  internal LogIndexService(ILogThumbnailRenderer? thumbnailRenderer = null) =>
      _thumbnailRenderer = thumbnailRenderer ?? new LogThumbnailRenderer();

  public async Task<IReadOnlyList<LogIndexEntry>> ScanAsync(
      string root, IProgress<LogIndexProgress>? progress, CancellationToken cancellationToken) {
    string normalizedRoot = ValidateRoot(root);
    string[] files = DiscoverFiles(normalizedRoot).ToArray();
    var entries = new ConcurrentBag<LogIndexEntry>();
    int completed = 0;
    await Parallel.ForEachAsync(files, new ParallelOptions {
      CancellationToken = cancellationToken,
      MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
    }, async (file, token) => {
      LogIndexEntry entry = await AnalyzeFileAsync(normalizedRoot, file, token).ConfigureAwait(false);
      entries.Add(entry);
      int done = Interlocked.Increment(ref completed);
      progress?.Report(new LogIndexProgress(done, files.Length, file));
    }).ConfigureAwait(false);

    return entries
        .OrderByDescending(entry => entry.Date == DateTime.MinValue
            ? entry.SourceLastWriteUtc
            : entry.Date.ToUniversalTime())
        .ThenBy(entry => entry.FullPath, PathComparer)
        .ToArray();
  }

  public Task<LogIndexDeleteResult> DeleteAsync(
      string root, IReadOnlyList<LogIndexEntry> entries, CancellationToken cancellationToken) =>
      Task.Run(() => DeleteCore(ValidateRoot(root), entries, cancellationToken), cancellationToken);

  internal static IEnumerable<string> DiscoverFiles(string root) {
    string normalizedRoot = ValidateRoot(root);
    var options = new EnumerationOptions {
      RecurseSubdirectories = true,
      IgnoreInaccessible = true,
      AttributesToSkip = FileAttributes.ReparsePoint,
      ReturnSpecialDirectories = false,
    };
    IEnumerable<string> files;
    try {
      files = Directory.EnumerateFiles(normalizedRoot, "*", options);
    } catch (UnauthorizedAccessException) {
      yield break;
    } catch (DirectoryNotFoundException) {
      yield break;
    }

    foreach (string file in files) {
      string extension = Path.GetExtension(file);
      if (extension.Equals(".tlog", StringComparison.OrdinalIgnoreCase) ||
          extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
          extension.Equals(".log", StringComparison.OrdinalIgnoreCase)) {
        yield return Path.GetFullPath(file);
      }
    }
  }

  internal static IReadOnlyList<string> AssociatedPaths(LogIndexEntry entry) {
    var paths = new List<string> { entry.FullPath, entry.FullPath + ".jpg" };
    if (Path.GetExtension(entry.FullPath).Equals(".tlog", StringComparison.OrdinalIgnoreCase)) {
      paths.Add(Path.ChangeExtension(entry.FullPath, ".rlog"));
    }
    return paths.Distinct(PathComparer).ToArray();
  }

  private async Task<LogIndexEntry> AnalyzeFileAsync(
      string root, string path, CancellationToken cancellationToken) {
    var file = new FileInfo(path);
    long size = 0;
    DateTime lastWriteUtc = DateTime.MinValue;
    LogFileStamp? pairedRlog = null;
    try {
      size = file.Length;
      lastWriteUtc = file.LastWriteTimeUtc;
      if (Path.GetExtension(path).Equals(".tlog", StringComparison.OrdinalIgnoreCase)) {
        var rlog = new FileInfo(Path.ChangeExtension(path, ".rlog"));
        if (rlog.Exists && (rlog.Attributes & FileAttributes.ReparsePoint) == 0) {
          pairedRlog = new LogFileStamp(rlog.Length, rlog.LastWriteTimeUtc);
        }
      }
    } catch {
    }

    LogAnalysis analysis;
    try {
      analysis = await Task.Run(() =>
          Path.GetExtension(path).Equals(".tlog", StringComparison.OrdinalIgnoreCase)
              ? AnalyzeTlog(path, cancellationToken)
              : AnalyzeDataFlash(path, cancellationToken), cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      analysis = new LogAnalysis { Error = ex.Message };
    }

    byte[] thumbnail;
    try {
      thumbnail = await _thumbnailRenderer.GetOrCreateAsync(
          path, analysis.Track, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      thumbnail = LogThumbnailRenderer.RenderFallback([], "Thumbnail error");
      analysis.Error = JoinErrors(analysis.Error, "Thumbnail: " + ex.Message);
    }

    return new LogIndexEntry(
        path,
        root,
        size,
        lastWriteUtc,
        analysis.Date,
        analysis.Frame,
        analysis.SystemId,
        analysis.Duration,
        analysis.Home,
        analysis.TimeInAir,
        analysis.DistanceMeters,
        analysis.CameraMessages,
        thumbnail,
        analysis.Error,
        pairedRlog);
  }

  private static LogAnalysis AnalyzeDataFlash(string path, CancellationToken cancellationToken) {
    var result = new LogAnalysis { Frame = "DFLog Unknown" };
    var flight = new FlightMetrics();
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
    using var cancellableStream = new CancellationReadStream(stream, cancellationToken);
    using var log = new DFLogBuffer(cancellableStream);
    int seen = 0;
    foreach (DFLog.DFItem item in log.GetEnumeratorType(new[] { "GPS", "CAM", "MSG", "PARM" })) {
      if ((seen++ & 0x1ff) == 0) {
        cancellationToken.ThrowIfCancellationRequested();
      }
      try {
        switch (item.msgtype) {
          case "CAM":
            result.CameraMessages++;
            break;
          case "MSG":
            string message = FirstValue(item, "Message", "Msg", "Text");
            string? frame = DetectFrame(message);
            if (frame != null) {
              result.Frame = frame;
            }
            break;
          case "PARM":
            if (string.Equals(FirstValue(item, "Name"), "SYSID_THISMAV",
                    StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(FirstValue(item, "Value"), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out int systemId)) {
              result.SystemId = systemId;
            }
            break;
          case "GPS":
            if (!int.TryParse(item["Status"], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int status) || status < 3 ||
                !TryDouble(item["Lat"], out double latitude) ||
                !TryDouble(item["Lng"], out double longitude) ||
                !TryDouble(item["Alt"], out double altitude)) {
              break;
            }
            _ = TryDouble(item["Spd"], out double speed);
            flight.Add(latitude, longitude, altitude, speed, item.time, armed: false);
            break;
        }
      } catch {
        // A malformed record must not discard metadata already recovered from the file.
      }
    }
    flight.ApplyTo(result);
    if (result.Date != DateTime.MinValue && result.Date.Year <= 1970) {
      // TimeUS without a GPS week is relative and must not be shown as year 0001.
      // Its deltas remain valid for duration and time-in-air calculations.
      result.Date = DateTime.MinValue;
    }
    return result;
  }

  private static LogAnalysis AnalyzeTlog(string path, CancellationToken cancellationToken) {
    var result = new LogAnalysis { Frame = "Unknown" };
    var flight = new FlightMetrics();
    var fixes = new Dictionary<byte, byte>();
    var armed = new Dictionary<byte, bool>();
    byte primarySystem = 0;
    DateTime packetStart = DateTime.MinValue;
    DateTime packetEnd = DateTime.MinValue;
    bool sitl = false;
    int packets = 0;

    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
    long snapshotLength = stream.Length;
    var parser = new MAVLink.MavlinkParse(true);
    while (stream.Position < snapshotLength) {
      if ((packets++ & 0x1ff) == 0) {
        cancellationToken.ThrowIfCancellationRequested();
      }
      long before = stream.Position;
      MAVLink.MAVLinkMessage? packet;
      try {
        packet = parser.ReadPacket(stream);
      } catch (EndOfStreamException) {
        break;
      } catch {
        if (stream.Position <= before) {
          stream.Position = Math.Min(snapshotLength, before + 1);
        }
        continue;
      }
      if (packet == null || packet.sysid == 255) {
        continue;
      }
      if (packet.rxtime != DateTime.MinValue) {
        if (packetStart == DateTime.MinValue || packet.rxtime < packetStart) {
          packetStart = packet.rxtime;
        }
        if (packet.rxtime > packetEnd) {
          packetEnd = packet.rxtime;
        }
      }

      switch ((MAVLink.MAVLINK_MSG_ID)packet.msgid) {
        case MAVLink.MAVLINK_MSG_ID.HEARTBEAT when packet.data is MAVLink.mavlink_heartbeat_t heartbeat:
          if (primarySystem == 0) {
            primarySystem = packet.sysid;
          }
          armed[packet.sysid] =
              ((MAVLink.MAV_MODE_FLAG)heartbeat.base_mode & MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0;
          if (packet.sysid == primarySystem) {
            result.Frame = ((MAVLink.MAV_TYPE)heartbeat.type).ToString();
          }
          break;
        case MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT when packet.data is MAVLink.mavlink_gps_raw_int_t gps:
          fixes[packet.sysid] = gps.fix_type;
          break;
        case MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT
            when packet.data is MAVLink.mavlink_global_position_int_t position:
          if (primarySystem == 0) {
            primarySystem = packet.sysid;
          }
          if (packet.sysid != primarySystem ||
              fixes.TryGetValue(packet.sysid, out byte fix) && fix < 3) {
            break;
          }
          double speed = Math.Sqrt((double)position.vx * position.vx +
                                   (double)position.vy * position.vy) / 100.0;
          flight.Add(position.lat / 1e7, position.lon / 1e7, position.alt / 1000.0,
              speed, packet.rxtime,
              armed.TryGetValue(packet.sysid, out bool isArmed) && isArmed);
          break;
        case MAVLink.MAVLINK_MSG_ID.CAMERA_FEEDBACK:
          result.CameraMessages++;
          break;
        case MAVLink.MAVLINK_MSG_ID.SIM_STATE:
        case MAVLink.MAVLINK_MSG_ID.SIMSTATE:
          sitl = true;
          break;
      }
    }

    result.SystemId = primarySystem;
    result.Date = packetStart;
    result.Duration = ValidDuration(packetStart, packetEnd);
    flight.ApplyTo(result, preserveDateAndDuration: true);
    if (sitl) {
      result.Frame += " (SITL)";
    }
    return result;
  }

  private static LogIndexDeleteResult DeleteCore(
      string root, IReadOnlyList<LogIndexEntry> entries, CancellationToken cancellationToken) {
    var deleted = new List<string>();
    var errors = new List<string>();
    foreach (LogIndexEntry entry in entries) {
      cancellationToken.ThrowIfCancellationRequested();
      try {
        string source = Path.GetFullPath(entry.FullPath);
        if (!string.Equals(Path.GetFullPath(entry.RootPath), root, PathComparison)) {
          throw new IOException("The log belongs to a different indexed root; rescan before deleting it.");
        }
        EnsureWithinRoot(root, source);
        var sourceInfo = new FileInfo(source);
        if (!sourceInfo.Exists) {
          throw new FileNotFoundException("The indexed log no longer exists.", source);
        }
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0) {
          throw new IOException("Refusing to delete a symbolic link or reparse-point log.");
        }
        if (sourceInfo.Length != entry.SizeBytes ||
            sourceInfo.LastWriteTimeUtc != entry.SourceLastWriteUtc) {
          throw new IOException("The log changed since it was indexed; rescan before deleting it.");
        }

        string[] targets = AssociatedPaths(entry).Where(File.Exists).ToArray();
        foreach (string target in targets) {
          string fullTarget = Path.GetFullPath(target);
          EnsureWithinRoot(root, fullTarget);
          if ((File.GetAttributes(fullTarget) & FileAttributes.ReparsePoint) != 0) {
            throw new IOException($"Refusing to delete linked companion file '{Path.GetFileName(target)}'.");
          }
          if (Path.GetExtension(source).Equals(".tlog", StringComparison.OrdinalIgnoreCase) &&
              string.Equals(fullTarget, Path.ChangeExtension(source, ".rlog"), PathComparison)) {
            var companion = new FileInfo(fullTarget);
            if (entry.PairedRlog == null) {
              throw new IOException("A paired .rlog appeared after indexing; rescan before deleting it.");
            }
            if (companion.Length != entry.PairedRlog.SizeBytes ||
                companion.LastWriteTimeUtc != entry.PairedRlog.LastWriteUtc) {
              throw new IOException("The paired .rlog changed since indexing; rescan before deleting it.");
            }
          }
        }
        // Remove the selected source first. A generated thumbnail cleanup failure
        // must never cause the more valuable paired telemetry to disappear while
        // the selected source remains and the UI still shows its row.
        File.Delete(source);
        deleted.Add(source);
        foreach (string target in targets.Where(path =>
                     !string.Equals(path, source, PathComparison))) {
          try {
            File.Delete(target);
          } catch (Exception ex) {
            errors.Add($"{Path.GetFileName(source)} companion {Path.GetFileName(target)}: {ex.Message}");
          }
        }
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        errors.Add(Path.GetFileName(entry.FullPath) + ": " + ex.Message);
      }
    }
    return new LogIndexDeleteResult(deleted, errors);
  }

  private static string ValidateRoot(string root) {
    if (string.IsNullOrWhiteSpace(root)) {
      throw new ArgumentException("Choose a log directory.", nameof(root));
    }
    string full = Path.GetFullPath(root);
    var info = new DirectoryInfo(full);
    if (!info.Exists) {
      throw new DirectoryNotFoundException($"Log directory does not exist: {full}");
    }
    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) {
      throw new IOException("A symbolic-link or reparse-point directory cannot be used as the log-index root.");
    }
    return full;
  }

  private static void EnsureWithinRoot(string root, string path) {
    string relative = Path.GetRelativePath(root, path);
    if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
            PathComparison) || Path.IsPathRooted(relative)) {
      throw new IOException("Indexed path escaped the selected log directory.");
    }
  }

  private static string FirstValue(DFLog.DFItem item, params string[] fields) {
    foreach (string field in fields) {
      string? value = item[field];
      if (!string.IsNullOrWhiteSpace(value)) {
        return value.Trim();
      }
    }
    return "";
  }

  private static string? DetectFrame(string message) {
    foreach (string name in new[] {
        "ArduCopter", "ArduPlane", "ArduRover", "ArduSub", "AntennaTracker", "ArduTracker",
      }) {
      if (message.Contains(name, StringComparison.OrdinalIgnoreCase)) {
        return name;
      }
    }
    return null;
  }

  private static bool TryDouble(string? text, out double value) =>
      double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
      double.IsFinite(value);

  private static TimeSpan ValidDuration(DateTime start, DateTime end) =>
      start == DateTime.MinValue || end < start ? TimeSpan.Zero : end - start;

  private static string JoinErrors(string? first, string second) =>
      string.IsNullOrWhiteSpace(first) ? second : first + "; " + second;

  private static StringComparer PathComparer => OperatingSystem.IsWindows()
      ? StringComparer.OrdinalIgnoreCase
      : StringComparer.Ordinal;

  private static StringComparison PathComparison => OperatingSystem.IsWindows()
      ? StringComparison.OrdinalIgnoreCase
      : StringComparison.Ordinal;

  private sealed class LogAnalysis {
    internal DateTime Date;
    internal string Frame = "Unknown";
    internal int SystemId;
    internal TimeSpan Duration;
    internal LogHome? Home;
    internal TimeSpan TimeInAir;
    internal double DistanceMeters;
    internal int CameraMessages;
    internal IReadOnlyList<(double Lat, double Lng)> Track = [];
    internal string? Error;
  }

  private sealed class FlightMetrics {
    private readonly List<(double Lat, double Lng)> _track = [];
    private Position? _lastAccepted;
    private Position? _lastDistance;
    private DateTime _start = DateTime.MinValue;
    private DateTime _end = DateTime.MinValue;
    private double _timeInAirSeconds;
    private double _distanceMeters;

    internal LogHome? Home { get; private set; }

    internal void Add(double latitude, double longitude, double altitude,
        double speed, DateTime time, bool armed) {
      if (!ValidCoordinate(latitude, longitude)) {
        return;
      }
      var current = new Position(latitude, longitude, altitude, time);
      if (_lastAccepted is { } previous) {
        double jump = Distance(previous.Latitude, previous.Longitude, latitude, longitude);
        double seconds = TimeDelta(previous.Time, time);
        double maximumJump = seconds > 0 ? 1000 + 300 * seconds : 100000;
        if (jump > maximumJump) {
          return;
        }
      }

      Home ??= new LogHome(latitude, longitude, altitude);
      _track.Add((latitude, longitude));
      if (time != DateTime.MinValue) {
        if (_start == DateTime.MinValue || time < _start) {
          _start = time;
        }
        if (time > _end) {
          _end = time;
        }
      }

      if (_lastDistance is { } distancePrevious) {
        double seconds = TimeDelta(distancePrevious.Time, time);
        if (seconds >= 1 || time == DateTime.MinValue || distancePrevious.Time == DateTime.MinValue) {
          _distanceMeters += Distance(
              distancePrevious.Latitude, distancePrevious.Longitude, latitude, longitude);
          _lastDistance = current;
        }
      } else {
        _lastDistance = current;
      }

      if (_lastAccepted is { } movementPrevious) {
        double seconds = TimeDelta(movementPrevious.Time, time);
        if (seconds is > 0 and <= 10 &&
            (armed || speed > 0.2 || altitude > (Home?.Altitude ?? altitude) + 2)) {
          _timeInAirSeconds += seconds;
        }
      }
      _lastAccepted = current;
    }

    internal void ApplyTo(LogAnalysis analysis, bool preserveDateAndDuration = false) {
      if (!preserveDateAndDuration || analysis.Date == DateTime.MinValue) {
        analysis.Date = _start;
      }
      if (!preserveDateAndDuration || analysis.Duration == TimeSpan.Zero) {
        analysis.Duration = ValidDuration(_start, _end);
      }
      analysis.Home = Home;
      analysis.TimeInAir = TimeSpan.FromSeconds(Math.Max(0, _timeInAirSeconds));
      analysis.DistanceMeters = Math.Max(0, _distanceMeters);
      analysis.Track = Decimate(_track, 4000);
    }

    private static bool ValidCoordinate(double latitude, double longitude) =>
        double.IsFinite(latitude) && double.IsFinite(longitude) &&
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 &&
        !(latitude == 0 && longitude == 0);

    private static double TimeDelta(DateTime first, DateTime second) =>
        first == DateTime.MinValue || second == DateTime.MinValue ? 0 : (second - first).TotalSeconds;

    private static double Distance(double lat1, double lon1, double lat2, double lon2) {
      const double radius = 6371000;
      double dLat = DegreesToRadians(lat2 - lat1);
      double dLon = DegreesToRadians(lon2 - lon1);
      double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
          Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
          Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
      return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1 - a)));
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180.0;

    private static IReadOnlyList<(double Lat, double Lng)> Decimate(
        IReadOnlyList<(double Lat, double Lng)> values, int maximum) {
      if (values.Count <= maximum) {
        return values.ToArray();
      }
      var result = new List<(double Lat, double Lng)>(maximum);
      for (int index = 0; index < maximum; index++) {
        int source = (int)Math.Round(index * (values.Count - 1.0) / (maximum - 1));
        result.Add(values[source]);
      }
      return result;
    }

    private sealed record Position(double Latitude, double Longitude, double Altitude, DateTime Time);
  }

  /// <summary>
  /// Makes DFLogBuffer's initial indexing pass cancellable and hides FileStream.Name,
  /// avoiding its BinaryFormatter cache path which is unsupported on modern .NET.
  /// </summary>
  private sealed class CancellationReadStream(Stream inner, CancellationToken cancellationToken)
      : Stream {
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) {
      cancellationToken.ThrowIfCancellationRequested();
      return inner.Read(buffer, offset, count);
    }
    public override int Read(Span<byte> buffer) {
      cancellationToken.ThrowIfCancellationRequested();
      return inner.Read(buffer);
    }
    public override int ReadByte() {
      cancellationToken.ThrowIfCancellationRequested();
      return inner.ReadByte();
    }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
    protected override void Dispose(bool disposing) {
      if (disposing) {
        inner.Dispose();
      }
      base.Dispose(disposing);
    }
  }
}

internal sealed class LogThumbnailRenderer : ILogThumbnailRenderer {
  private const int _width = 240;
  private const int _height = 140;

  public async Task<byte[]> GetOrCreateAsync(
      string sourcePath, IReadOnlyList<(double Lat, double Lng)> track,
      CancellationToken cancellationToken) {
    string sidecar = sourcePath + ".jpg";
    DateTime sourceWrite = File.GetLastWriteTimeUtc(sourcePath);
    bool preserveExisting = IsFreshSidecarPresent(sidecar, sourceWrite);
    if (TryReadFreshSidecar(sidecar, sourceWrite, out byte[]? cached)) {
      return cached!;
    }

    byte[] rendered = track.Count < 2
        ? RenderFallback(track, "No GPS data")
        : await RenderTrackAsync(track, cancellationToken).ConfigureAwait(false);
    if (!preserveExisting) {
      TryWriteSidecar(sourcePath, sidecar, sourceWrite, rendered);
    }
    return rendered;
  }

  internal static byte[] RenderFallback(
      IReadOnlyList<(double Lat, double Lng)> track, string text) {
    using var bitmap = new SKBitmap(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap);
    DrawGrid(canvas);
    if (track.Count >= 2) {
      DrawRoute(canvas, track, Bounds(track));
    } else {
      using var paint = new SKPaint {
        Color = new SKColor(235, 120, 100),
        IsAntialias = true,
      };
      using var font = new SKFont(SKTypeface.Default, 18);
      canvas.DrawText(text, 12, 28, SKTextAlign.Left, font, paint);
    }
    return Encode(bitmap);
  }

  private static async Task<byte[]> RenderTrackAsync(
      IReadOnlyList<(double Lat, double Lng)> track, CancellationToken cancellationToken) {
    GeoBounds bounds = Bounds(track).FitAspect(_width / (double)_height, 0.12);
    using var bitmap = new SKBitmap(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap);
    DrawGrid(canvas);

    var schema = new GlobalSphericalMercator("png", minZoomLevel: 0, maxZoomLevel: 21);
    int zoom = 1;
    for (int level = 16; level >= 1; level--) {
      double resolution = schema.Resolutions[level].UnitsPerPixel;
      if (bounds.Width / resolution <= _width && bounds.Height / resolution <= _height) {
        zoom = level;
        break;
      }
    }
    var extent = new Extent(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
    IReadOnlyList<TileInfo> tiles = schema.GetTileInfos(extent, zoom).Take(12).ToArray();
    string mapType = MapTileSourceFactory.CurrentMapType;
    foreach (TileInfo tile in tiles) {
      cancellationToken.ThrowIfCancellationRequested();
      byte[]? data;
      try {
        data = await MapTileSourceFactory.GetCachedTileAsync(
            mapType, tile, cancellationToken).ConfigureAwait(false);
      } catch when (!cancellationToken.IsCancellationRequested) {
        data = null;
      }
      if (data is not { Length: > 0 }) {
        continue;
      }
      using SKBitmap? tileBitmap = SKBitmap.Decode(data);
      if (tileBitmap == null) {
        continue;
      }
      float left = (float)((tile.Extent.MinX - bounds.MinX) / bounds.Width * _width);
      float right = (float)((tile.Extent.MaxX - bounds.MinX) / bounds.Width * _width);
      float top = (float)((bounds.MaxY - tile.Extent.MaxY) / bounds.Height * _height);
      float bottom = (float)((bounds.MaxY - tile.Extent.MinY) / bounds.Height * _height);
      canvas.DrawBitmap(tileBitmap, new SKRect(left, top, right, bottom));
    }
    DrawRoute(canvas, track, bounds);
    return Encode(bitmap);
  }

  private static void DrawGrid(SKCanvas canvas) {
    canvas.Clear(new SKColor(36, 43, 48));
    using var grid = new SKPaint { Color = new SKColor(62, 72, 78), StrokeWidth = 1 };
    for (int x = 0; x < _width; x += 24) {
      canvas.DrawLine(x, 0, x, _height, grid);
    }
    for (int y = 0; y < _height; y += 20) {
      canvas.DrawLine(0, y, _width, y, grid);
    }
  }

  private static void DrawRoute(SKCanvas canvas,
      IReadOnlyList<(double Lat, double Lng)> track, GeoBounds bounds) {
    using var halo = new SKPaint {
      Color = new SKColor(255, 255, 255, 210),
      StrokeWidth = 5,
      IsAntialias = true,
      Style = SKPaintStyle.Stroke,
    };
    using var route = new SKPaint {
      Color = new SKColor(230, 45, 45),
      StrokeWidth = 2.5f,
      IsAntialias = true,
      Style = SKPaintStyle.Stroke,
    };
    using var path = new SKPath();
    for (int index = 0; index < track.Count; index++) {
      var (x, y) = SphericalMercator.FromLonLat(track[index].Lng, track[index].Lat);
      float px = (float)((x - bounds.MinX) / bounds.Width * _width);
      float py = (float)((bounds.MaxY - y) / bounds.Height * _height);
      if (index == 0) {
        path.MoveTo(px, py);
      } else {
        path.LineTo(px, py);
      }
    }
    canvas.DrawPath(path, halo);
    canvas.DrawPath(path, route);
    var start = path.GetPoint(0);
    var end = path.GetPoint(path.PointCount - 1);
    using var startPaint = new SKPaint { Color = SKColors.LimeGreen, IsAntialias = true };
    using var endPaint = new SKPaint { Color = SKColors.Red, IsAntialias = true };
    canvas.DrawCircle(start, 5, startPaint);
    canvas.DrawCircle(end, 5, endPaint);
  }

  private static GeoBounds Bounds(IReadOnlyList<(double Lat, double Lng)> track) {
    double minX = double.PositiveInfinity;
    double minY = double.PositiveInfinity;
    double maxX = double.NegativeInfinity;
    double maxY = double.NegativeInfinity;
    foreach (var point in track) {
      var (x, y) = SphericalMercator.FromLonLat(point.Lng, point.Lat);
      minX = Math.Min(minX, x);
      minY = Math.Min(minY, y);
      maxX = Math.Max(maxX, x);
      maxY = Math.Max(maxY, y);
    }
    if (!double.IsFinite(minX)) {
      return new GeoBounds(-100, -100, 100, 100);
    }
    if (maxX - minX < 100) {
      minX -= 50;
      maxX += 50;
    }
    if (maxY - minY < 100) {
      minY -= 50;
      maxY += 50;
    }
    return new GeoBounds(minX, minY, maxX, maxY);
  }

  private static bool TryReadFreshSidecar(
      string sidecar, DateTime sourceWrite, out byte[]? bytes) {
    bytes = null;
    try {
      var info = new FileInfo(sidecar);
      if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
          info.LastWriteTimeUtc < sourceWrite || info.Length is <= 0 or > 20 * 1024 * 1024) {
        return false;
      }
      byte[] candidate;
      using (var stream = new FileStream(sidecar, FileMode.Open, FileAccess.Read,
                 FileShare.ReadWrite | FileShare.Delete)) {
        candidate = new byte[stream.Length];
        stream.ReadExactly(candidate);
      }
      using SKBitmap? bitmap = SKBitmap.Decode(candidate);
      if (bitmap == null) {
        return false;
      }
      bytes = candidate;
      return true;
    } catch {
      return false;
    }
  }

  private static bool IsFreshSidecarPresent(string sidecar, DateTime sourceWrite) {
    try {
      var info = new FileInfo(sidecar);
      return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
             info.LastWriteTimeUtc >= sourceWrite;
    } catch {
      return false;
    }
  }

  private static void TryWriteSidecar(
      string source, string sidecar, DateTime sourceWrite, byte[] bytes) {
    string? temporary = null;
    try {
      var sourceInfo = new FileInfo(source);
      if (!sourceInfo.Exists || sourceInfo.LastWriteTimeUtc != sourceWrite ||
          (sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0) {
        return;
      }
      if (File.Exists(sidecar) &&
          (File.GetAttributes(sidecar) & FileAttributes.ReparsePoint) != 0) {
        return;
      }
      temporary = sidecar + "." + Guid.NewGuid().ToString("N") + ".partial";
      using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                 FileShare.None, 81920, FileOptions.WriteThrough)) {
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
      }
      File.Move(temporary, sidecar, overwrite: true);
      temporary = null;
      File.SetLastWriteTimeUtc(sidecar, sourceWrite);
    } catch {
      // Read-only media and concurrently changing logs still get an in-memory thumbnail.
    } finally {
      if (temporary != null) {
        try {
          File.Delete(temporary);
        } catch {
        }
      }
    }
  }

  private static byte[] Encode(SKBitmap bitmap) {
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, 86);
    return encoded.ToArray();
  }

  private sealed record GeoBounds(double MinX, double MinY, double MaxX, double MaxY) {
    internal double Width => Math.Max(1, MaxX - MinX);
    internal double Height => Math.Max(1, MaxY - MinY);

    internal GeoBounds FitAspect(double targetAspect, double marginFraction) {
      double centerX = (MinX + MaxX) / 2;
      double centerY = (MinY + MaxY) / 2;
      double width = Width * (1 + marginFraction * 2);
      double height = Height * (1 + marginFraction * 2);
      if (width / height > targetAspect) {
        height = width / targetAspect;
      } else {
        width = height * targetAspect;
      }
      return new GeoBounds(centerX - width / 2, centerY - height / 2,
          centerX + width / 2, centerY + height / 2);
    }
  }
}
