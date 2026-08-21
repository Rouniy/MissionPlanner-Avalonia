using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Log;
using MissionPlanner.Utilities;

namespace MissionPlannerAvalonia.Services;

public readonly record struct MagVector(double X, double Y, double Z) {
  public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

  public override string ToString() =>
      $"{X.ToString("0.00", CultureInfo.InvariantCulture)}, "
      + $"{Y.ToString("0.00", CultureInfo.InvariantCulture)}, "
      + Z.ToString("0.00", CultureInfo.InvariantCulture);
}

public sealed record OfflineMagFitResult(
    int Compass,
    int SourceSamples,
    int UsedSamples,
    int CoverageOctants,
    MagVector LoggedOffsets,
    MagVector SphereOffsets,
    double SphereRadius,
    double SphereRmsError,
    MagVector Offsets,
    MagVector Diagonals,
    MagVector OffDiagonals,
    double RmsError,
    bool HasEllipsoid) {
  public string CompassName => $"Compass {Compass}";
  public string CoverageText => $"{CoverageOctants}/8 octants";
  public string ModelName => HasEllipsoid ? "Ellipsoid" : "Sphere";
}

public sealed record OfflineMagFitReport(
    string SourcePath,
    IReadOnlyList<OfflineMagFitResult> Results,
    int ThrottleThreshold,
    bool IsTelemetryLog);

/// <summary>
/// Cross-platform port of MissionPlanner.MagCalib's offline log calibration. It keeps
/// the official raw-minus-logged-offset sign convention and ALGLIB LM fit, while also
/// separating modern instance-tagged MAG records and returning all three compasses.
/// </summary>
public static class OfflineMagFitService {
  private const int _minimumSamples = 10;
  private const int _maximumSamplesPerCompass = 1_000_000;
  private const double _bucketSize = 20;

  public static Task<OfflineMagFitReport> AnalyzeAsync(
      string path,
      int throttleThreshold = 0,
      bool ellipsoid = true,
      IProgress<double>? progress = null,
      CancellationToken cancellationToken = default) => Task.Run(
      () => Analyze(path, throttleThreshold, ellipsoid, progress, cancellationToken),
      cancellationToken);

  public static OfflineMagFitReport Analyze(
      string path,
      int throttleThreshold = 0,
      bool ellipsoid = true,
      IProgress<double>? progress = null,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    string fullPath = Path.GetFullPath(path);
    if (!File.Exists(fullPath)) {
      throw new FileNotFoundException("The selected flight log does not exist.", fullPath);
    }

    string extension = Path.GetExtension(fullPath);
    bool telemetry = extension.Equals(".tlog", StringComparison.OrdinalIgnoreCase);
    if (!telemetry && !extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
        && !extension.Equals(".log", StringComparison.OrdinalIgnoreCase)) {
      throw new NotSupportedException("Offline MagFit accepts .tlog, .bin and .log files.");
    }

    throttleThreshold = Math.Clamp(throttleThreshold, 0, 100);
    cancellationToken.ThrowIfCancellationRequested();
    Dictionary<int, SampleSet> sets = telemetry
        ? ReadTelemetry(fullPath, throttleThreshold, progress, cancellationToken)
        : ReadDataFlash(fullPath, progress, cancellationToken);
    if (sets.Count == 0) {
      string detail = telemetry && throttleThreshold > 0
          ? $" above the {throttleThreshold}% throttle threshold"
          : "";
      throw new InvalidDataException("The log contains no usable magnetometer samples" + detail + ".");
    }

    var results = new List<OfflineMagFitResult>();
    int completed = 0;
    foreach ((int compass, SampleSet set) in sets.OrderBy(item => item.Key)) {
      cancellationToken.ThrowIfCancellationRequested();
      IReadOnlyList<MagVector> samples = telemetry
          ? PrepareTelemetrySamples(set.Samples)
          : set.Samples;
      if (samples.Count < _minimumSamples) {
        throw new InvalidDataException(
            $"Compass {compass} has only {samples.Count} usable samples; at least {_minimumSamples} are required.");
      }
      results.Add(FitSamples(compass, samples, set.Samples.Count, set.LastLoggedOffsets,
          ellipsoid, cancellationToken));
      completed++;
      progress?.Report(0.75 + 0.25 * completed / sets.Count);
    }

    return new OfflineMagFitReport(fullPath, results, throttleThreshold, telemetry);
  }

  internal static OfflineMagFitResult FitSamples(
      int compass,
      IReadOnlyList<MagVector> samples,
      int sourceSamples,
      MagVector loggedOffsets,
      bool ellipsoid,
      CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    if (samples.Count < _minimumSamples) {
      throw new InvalidDataException($"At least {_minimumSamples} magnetometer samples are required.");
    }
    if (samples.Any(sample => !Finite(sample))) {
      throw new InvalidDataException("Magnetometer samples contain NaN or infinity.");
    }

    double meanRadius = samples.Average(sample => sample.Length);
    if (!double.IsFinite(meanRadius) || meanRadius <= double.Epsilon) {
      throw new InvalidDataException("Magnetometer samples have no measurable radius.");
    }

    double[] sphere = Solve(samples, [0, 0, 0, meanRadius],
        static (parameters, sample, _) => {
          double dx = sample.X + parameters[0];
          double dy = sample.Y + parameters[1];
          double dz = sample.Z + parameters[2];
          return parameters[3] - Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }, meanRadius, cancellationToken);
    var sphereOffsets = new MagVector(sphere[0], sphere[1], sphere[2]);
    double sphereRadius = sphere[3];
    double sphereRms = Rms(samples, sample => SphereResidual(sample, sphereOffsets, sphereRadius));

    MagVector offsets = sphereOffsets;
    var diagonals = new MagVector(1, 1, 1);
    var offDiagonals = new MagVector(0, 0, 0);
    double rms = sphereRms;
    if (ellipsoid) {
      // Official MagCalib performs the same two stages. Unlike its current residual,
      // this version actually applies p[0..2], so the optimized offsets are not inert.
      double[] diagonalFit = Solve(samples,
          [sphere[0], sphere[1], sphere[2], 1, 1, 1],
          EllipsoidResidual, meanRadius, cancellationToken);
      NormalizeDiagonals(diagonalFit);
      double[] fullFit = Solve(samples,
          [diagonalFit[0], diagonalFit[1], diagonalFit[2],
           diagonalFit[3], diagonalFit[4], diagonalFit[5], 0, 0, 0],
          EllipsoidResidual, meanRadius, cancellationToken);
      NormalizeDiagonals(fullFit);
      offsets = new MagVector(fullFit[0], fullFit[1], fullFit[2]);
      diagonals = new MagVector(fullFit[3], fullFit[4], fullFit[5]);
      offDiagonals = new MagVector(fullFit[6], fullFit[7], fullFit[8]);
      rms = Rms(samples, sample => EllipsoidResidual(fullFit, sample, meanRadius));
    }

    if (!Finite(offsets) || !Finite(diagonals) || !Finite(offDiagonals)
        || !double.IsFinite(rms)) {
      throw new InvalidDataException("MagFit did not converge to finite calibration values.");
    }

    return new OfflineMagFitResult(
        compass,
        sourceSamples,
        samples.Count,
        CoverageOctants(samples, offsets),
        loggedOffsets,
        sphereOffsets,
        sphereRadius,
        sphereRms,
        offsets,
        diagonals,
        offDiagonals,
        rms,
        ellipsoid);
  }

  internal static IReadOnlyList<MagVector> PrepareTelemetrySamples(
      IReadOnlyList<MagVector> source) {
    var buckets = new Dictionary<(int X, int Y, int Z), int>();
    var filtered = new List<MagVector>(source.Count);
    foreach (MagVector sample in source) {
      var key = ((int)(sample.X / _bucketSize),
                 (int)(sample.Y / _bucketSize),
                 (int)(sample.Z / _bucketSize));
      buckets.TryGetValue(key, out int count);
      if (count >= 3) {
        continue;
      }
      buckets[key] = count + 1;
      filtered.Add(sample);
    }

    filtered.Sort(static (left, right) => left.Length.CompareTo(right.Length));
    int remove = filtered.Count / 16;
    if (remove > 0) {
      filtered.RemoveRange(filtered.Count - remove, remove);
    }
    return filtered;
  }

  private static Dictionary<int, SampleSet> ReadDataFlash(
      string path, IProgress<double>? progress, CancellationToken cancellationToken) {
    var sets = new Dictionary<int, SampleSet>();
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
    using var cancellable = new CancellationReadStream(stream, cancellationToken);
    using var log = new DFLogBuffer(cancellable);
    int seen = 0;
    foreach (DFLog.DFItem item in log.GetEnumeratorType(["MAG", "MAG2", "MAG3"])) {
      if ((seen++ & 0x3ff) == 0) {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(Math.Min(0.7, 0.7 * StreamFraction(cancellable)));
      }
      int compass = ResolveCompass(item);
      if (!TryRead(item, ["MagX", "X"], out double x)
          || !TryRead(item, ["MagY", "Y"], out double y)
          || !TryRead(item, ["MagZ", "Z"], out double z)) {
        continue;
      }
      double ox = ReadOrZero(item, ["OfsX", "OX"]);
      double oy = ReadOrZero(item, ["OfsY", "OY"]);
      double oz = ReadOrZero(item, ["OfsZ", "OZ"]);
      Add(sets, compass, new MagVector(x - ox, y - oy, z - oz), new MagVector(ox, oy, oz));
    }
    progress?.Report(0.7);
    return sets;
  }

  private static Dictionary<int, SampleSet> ReadTelemetry(
      string path,
      int throttleThreshold,
      IProgress<double>? progress,
      CancellationToken cancellationToken) {
    var sets = new Dictionary<int, SampleSet>();
    var loggedOffsets = new MagVector();
    bool useData = throttleThreshold <= 0;
    int packets = 0;
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
    long length = stream.Length;
    var parser = new MAVLink.MavlinkParse(true);
    while (stream.Position < length) {
      if ((packets++ & 0x1ff) == 0) {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(length == 0 ? 0 : 0.7 * stream.Position / length);
      }
      long before = stream.Position;
      MAVLink.MAVLinkMessage? packet;
      try {
        packet = parser.ReadPacket(stream);
      } catch (EndOfStreamException) {
        break;
      } catch (OperationCanceledException) {
        throw;
      } catch {
        if (stream.Position <= before) {
          stream.Position = Math.Min(length, before + 1);
        }
        continue;
      }
      if (packet == null) {
        continue;
      }

      switch ((MAVLink.MAVLINK_MSG_ID)packet.msgid) {
        case MAVLink.MAVLINK_MSG_ID.VFR_HUD
            when packet.data is MAVLink.mavlink_vfr_hud_t hud:
          useData = hud.throttle >= throttleThreshold;
          break;
#pragma warning disable CS0612 // Official MagCalib tlog compatibility uses legacy SENSOR_OFFSETS.
        case MAVLink.MAVLINK_MSG_ID.SENSOR_OFFSETS
            when packet.data is MAVLink.mavlink_sensor_offsets_t sensorOffsets:
          loggedOffsets = new MagVector(
              sensorOffsets.mag_ofs_x, sensorOffsets.mag_ofs_y, sensorOffsets.mag_ofs_z);
          break;
#pragma warning restore CS0612
        case MAVLink.MAVLINK_MSG_ID.RAW_IMU
            when useData && packet.data is MAVLink.mavlink_raw_imu_t imu:
          Add(sets, 1,
              new MagVector(imu.xmag - loggedOffsets.X,
                            imu.ymag - loggedOffsets.Y,
                            imu.zmag - loggedOffsets.Z),
              loggedOffsets);
          break;
        case MAVLink.MAVLINK_MSG_ID.SCALED_IMU2
            when useData && packet.data is MAVLink.mavlink_scaled_imu2_t imu2:
          Add(sets, 2, new MagVector(imu2.xmag, imu2.ymag, imu2.zmag), default);
          break;
        case MAVLink.MAVLINK_MSG_ID.SCALED_IMU3
            when useData && packet.data is MAVLink.mavlink_scaled_imu3_t imu3:
          Add(sets, 3, new MagVector(imu3.xmag, imu3.ymag, imu3.zmag), default);
          break;
      }
    }
    progress?.Report(0.7);
    return sets;
  }

  private static int ResolveCompass(DFLog.DFItem item) {
    if (item.msgtype.Equals("MAG2", StringComparison.OrdinalIgnoreCase)) {
      return 2;
    }
    if (item.msgtype.Equals("MAG3", StringComparison.OrdinalIgnoreCase)) {
      return 3;
    }
    if (TryRead(item, ["I", "Instance", "C"], out double instance)
        && instance >= 0 && instance <= 2 && Math.Abs(instance - Math.Round(instance)) < 0.001) {
      return (int)Math.Round(instance) + 1;
    }
    return 1;
  }

  private static void Add(
      IDictionary<int, SampleSet> sets, int compass, MagVector sample, MagVector loggedOffsets) {
    if (compass is < 1 or > 3 || !Finite(sample)
        || sample is { X: 0, Y: 0, Z: 0 }) {
      return;
    }
    if (!sets.TryGetValue(compass, out SampleSet? set)) {
      set = new SampleSet();
      sets.Add(compass, set);
    }
    if (set.Samples.Count >= _maximumSamplesPerCompass) {
      throw new InvalidDataException(
          $"Compass {compass} exceeds the safe limit of {_maximumSamplesPerCompass:N0} samples.");
    }
    set.Samples.Add(sample);
    set.LastLoggedOffsets = loggedOffsets;
  }

  private static double[] Solve(
      IReadOnlyList<MagVector> samples,
      double[] initial,
      Func<double[], MagVector, double, double> residual,
      double radius,
      CancellationToken cancellationToken) {
    var context = new FitContext(samples, residual, radius, cancellationToken);
    alglib.minlmcreatev(samples.Count, initial, 0.1, out alglib.minlmstate state);
    alglib.minlmsetcond(state, 0, 100);
    alglib.minlmsetxrep(state, true);
    alglib.minlmoptimize(state,
        static (parameters, errors, stateObject) => {
          var fit = (FitContext)stateObject;
          fit.CancellationToken.ThrowIfCancellationRequested();
          for (int index = 0; index < fit.Samples.Count; index++) {
            errors[index] = fit.Residual(parameters, fit.Samples[index], fit.Radius);
          }
        },
        static (_, _, stateObject) =>
            ((FitContext)stateObject).CancellationToken.ThrowIfCancellationRequested(),
        context);
    alglib.minlmresults(state, out double[] fitted, out alglib.minlmreport report);
    if (report.terminationtype <= 0 || fitted.Any(value => !double.IsFinite(value))) {
      throw new InvalidDataException(
          $"MagFit solver did not converge (termination {report.terminationtype}).");
    }
    return fitted;
  }

  private static double SphereResidual(MagVector sample, MagVector offsets, double radius) {
    double x = sample.X + offsets.X;
    double y = sample.Y + offsets.Y;
    double z = sample.Z + offsets.Z;
    return radius - Math.Sqrt(x * x + y * y + z * z);
  }

  private static double EllipsoidResidual(double[] p, MagVector sample, double radius) {
    double x = sample.X + p[0];
    double y = sample.Y + p[1];
    double z = sample.Z + p[2];
    double dx = p[3];
    double dy = p[4];
    double dz = p[5];
    double oxy = p.Length > 6 ? p[6] : 0;
    double oxz = p.Length > 7 ? p[7] : 0;
    double oyz = p.Length > 8 ? p[8] : 0;
    NormalizeDiagonals(ref dx, ref dy, ref dz);
    double tx = dx * x + oxy * y + oxz * z;
    double ty = oxy * x + dy * y + oyz * z;
    double tz = oxz * x + oyz * y + dz * z;
    return radius - Math.Sqrt(tx * tx + ty * ty + tz * tz);
  }

  private static void NormalizeDiagonals(double[] values) {
    double x = values[3];
    double y = values[4];
    double z = values[5];
    NormalizeDiagonals(ref x, ref y, ref z);
    values[3] = x;
    values[4] = y;
    values[5] = z;
  }

  private static void NormalizeDiagonals(ref double x, ref double y, ref double z) {
    double length = Math.Sqrt(x * x + y * y + z * z);
    if (length <= double.Epsilon || !double.IsFinite(length)) {
      return;
    }
    double scale = Math.Sqrt(3) / length;
    x *= scale;
    y *= scale;
    z *= scale;
  }

  private static double Rms(IReadOnlyList<MagVector> samples, Func<MagVector, double> residual) =>
      Math.Sqrt(samples.Average(sample => {
        double value = residual(sample);
        return value * value;
      }));

  private static int CoverageOctants(IReadOnlyList<MagVector> samples, MagVector offsets) => samples
      .Select(sample => (sample.X + offsets.X >= 0 ? 1 : 0)
                      | (sample.Y + offsets.Y >= 0 ? 2 : 0)
                      | (sample.Z + offsets.Z >= 0 ? 4 : 0))
      .Distinct()
      .Count();

  private static bool TryRead(DFLog.DFItem item, IReadOnlyList<string> names, out double value) {
    foreach (string name in names) {
      string? raw = item[name];
      if (raw != null && double.TryParse(raw, NumberStyles.Float,
              CultureInfo.InvariantCulture, out value) && double.IsFinite(value)) {
        return true;
      }
    }
    value = 0;
    return false;
  }

  private static double ReadOrZero(DFLog.DFItem item, IReadOnlyList<string> names) =>
      TryRead(item, names, out double value) ? value : 0;

  private static bool Finite(MagVector value) =>
      double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

  private static double StreamFraction(Stream stream) =>
      stream.Length <= 0 ? 0 : Math.Clamp(stream.Position / (double)stream.Length, 0, 1);

  private sealed class SampleSet {
    public List<MagVector> Samples { get; } = [];
    public MagVector LastLoggedOffsets { get; set; }
  }

  private sealed record FitContext(
      IReadOnlyList<MagVector> Samples,
      Func<double[], MagVector, double, double> Residual,
      double Radius,
      CancellationToken CancellationToken);

  /// <summary>
  /// Makes DFLogBuffer's initial indexing pass cancellable and hides FileStream.Name,
  /// avoiding its BinaryFormatter cache path on modern .NET for large logs.
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
