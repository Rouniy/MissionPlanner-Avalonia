using System.Globalization;
using MissionPlanner;
using MissionPlannerAvalonia.Services;
using MissionPlannerAvalonia.ViewModels;

namespace MissionPlannerAvalonia.Tests;

public class OfflineMagFitTests {
  [Fact]
  public void Sphere_fit_recovers_official_additive_offset_sign() {
    var expected = new MagVector(42, -27, 13);
    MagVector[] samples = Sphere(320, 480, expected).ToArray();

    OfflineMagFitResult result = OfflineMagFitService.FitSamples(
        1, samples, samples.Length, new MagVector(10, 20, 30), ellipsoid: false);

    AssertVector(expected, result.Offsets, 0.05);
    Assert.InRange(result.SphereRadius, 479.95, 480.05);
    Assert.InRange(result.RmsError, 0, 0.01);
    Assert.Equal(8, result.CoverageOctants);
    Assert.False(result.HasEllipsoid);
  }

  [Fact]
  public void Ellipsoid_fit_reduces_soft_iron_error_and_recovers_center() {
    var expected = new MagVector(35, -22, 17);
    var samples = new List<MagVector>();
    foreach (MagVector point in Sphere(420, 500, default)) {
      samples.Add(new MagVector(
          point.X / 1.22 - expected.X,
          point.Y / 0.82 - expected.Y,
          point.Z / 0.98 - expected.Z));
    }

    OfflineMagFitResult result = OfflineMagFitService.FitSamples(
        1, samples, samples.Count, default, ellipsoid: true);

    Assert.True(result.HasEllipsoid);
    Assert.True(result.RmsError < result.SphereRmsError * 0.6,
        $"ellipsoid RMS {result.RmsError} was not better than sphere RMS {result.SphereRmsError}");
    AssertVector(expected, result.Offsets, 1.0);
    Assert.InRange(result.Diagonals.X, 1.15, 1.3);
    Assert.InRange(result.Diagonals.Y, 0.75, 0.9);
    Assert.InRange(result.Diagonals.Z, 0.9, 1.05);
  }

  [Fact]
  public void DataFlash_reader_separates_modern_instance_tagged_compasses() {
    var firstOffset = new MagVector(31, -18, 7);
    var secondOffset = new MagVector(-26, 14, -9);
    using var log = TextLog.Create(Enumerable.Range(0, 240).SelectMany(index => {
      MagVector a = SpherePoint(index, 240, 430, firstOffset);
      MagVector b = SpherePoint(index, 240, 510, secondOffset);
      return new[] {
        MagLine(index * 2L + 1, 0, a, new MagVector(4, 5, 6)),
        MagLine(index * 2L + 2, 1, b, new MagVector(-3, 2, 1)),
      };
    }));

    OfflineMagFitReport report = OfflineMagFitService.Analyze(
        log.Path, ellipsoid: false);

    Assert.Equal(2, report.Results.Count);
    AssertVector(firstOffset, report.Results[0].Offsets, 0.1);
    AssertVector(secondOffset, report.Results[1].Offsets, 0.1);
    Assert.Equal(new MagVector(4, 5, 6), report.Results[0].LoggedOffsets);
    Assert.Equal(new MagVector(-3, 2, 1), report.Results[1].LoggedOffsets);
  }

  [Fact]
  public void DataFlash_reader_accepts_legacy_MAG2_names_and_missing_logged_offsets() {
    var expected = new MagVector(12, 21, -17);
    using var log = TextLog.CreateLegacy(
        Enumerable.Range(0, 180).Select(index => {
          MagVector point = SpherePoint(index, 180, 390, expected);
          return FormattableString.Invariant(
              $"MAG2, {index * 1000L}, {point.X}, {point.Y}, {point.Z}");
        }));

    OfflineMagFitResult result = Assert.Single(
        OfflineMagFitService.Analyze(log.Path, ellipsoid: false).Results);

    Assert.Equal(2, result.Compass);
    AssertVector(expected, result.Offsets, 0.1);
    Assert.Equal(default, result.LoggedOffsets);
  }

  [Fact]
  public void Telemetry_filter_caps_dense_buckets_and_drops_outer_sixteenth() {
    var samples = new List<MagVector>();
    for (int index = 0; index < 10; index++) {
      samples.Add(new MagVector(101 + index * 0.1, 102, 103));
    }
    for (int index = 0; index < 29; index++) {
      samples.Add(new MagVector(200 + index * 25, index * 23, -index * 21));
    }
    samples.Add(new MagVector(50_000, 50_000, 50_000));

    IReadOnlyList<MagVector> filtered = OfflineMagFitService.PrepareTelemetrySamples(samples);

    Assert.Equal(31, filtered.Count); // 3 dense + 29 unique + outlier, then top 2/33.
    Assert.Equal(3, filtered.Count(sample => sample.X is >= 100 and < 120));
    Assert.DoesNotContain(filtered, sample => sample.X == 50_000);
  }

  [Fact]
  public void Pre_cancelled_fit_does_not_enter_solver() {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    Assert.Throws<OperationCanceledException>(() => OfflineMagFitService.FitSamples(
        1, Sphere(40, 300, default).ToArray(), 40, default, true, cancellation.Token));
  }

  [Fact]
  public void Tlog_honours_throttle_threshold_and_logged_offsets() {
    var expected = new MagVector(18, -12, 8);
    var logged = new MagVector(7, -4, 3);
    using var log = Tlog.Create();
#pragma warning disable CS0612 // Exercise official legacy tlog calibration input.
    log.Write(MAVLink.MAVLINK_MSG_ID.SENSOR_OFFSETS,
        new MAVLink.mavlink_sensor_offsets_t(0, 0, 0, 0, 0, 0, 0, 0, 0,
            (short)logged.X, (short)logged.Y, (short)logged.Z));
#pragma warning restore CS0612
    log.Write(MAVLink.MAVLINK_MSG_ID.VFR_HUD,
        new MAVLink.mavlink_vfr_hud_t(0, 0, 0, 0, 0, 10));
    foreach (MagVector point in Sphere(80, 350, expected)) {
      log.WriteRaw(point, logged);
    }
    log.Write(MAVLink.MAVLINK_MSG_ID.VFR_HUD,
        new MAVLink.mavlink_vfr_hud_t(0, 0, 0, 0, 0, 60));
    foreach (MagVector point in Sphere(240, 420, expected)) {
      log.WriteRaw(point, logged);
    }

    OfflineMagFitResult result = Assert.Single(
        OfflineMagFitService.Analyze(log.Path, throttleThreshold: 30, ellipsoid: false).Results);

    Assert.Equal(240, result.SourceSamples);
    Assert.True(result.UsedSamples < result.SourceSamples);
    Assert.Equal(logged, result.LoggedOffsets);
    AssertVector(expected, result.Offsets, 3.0);
  }

  [Fact]
  public void Parameter_plan_uses_official_numbered_names_and_validates_before_writing() {
    using var port = new MAVLinkInterface();
    string[] names = [
      "COMPASS_LEARN",
      "COMPASS_OFS2_X", "COMPASS_OFS2_Y", "COMPASS_OFS2_Z",
      "COMPASS_DIA2_X", "COMPASS_DIA2_Y", "COMPASS_DIA2_Z",
      "COMPASS_ODI2_X", "COMPASS_ODI2_Y", "COMPASS_ODI2_Z",
    ];
    foreach (string name in names) {
      port.MAV.param.Add(new MAVLink.MAVLinkParam(name, 0, MAVLink.MAV_PARAM_TYPE.REAL32));
    }
    var result = new OfflineMagFitResult(
        2, 100, 90, 8, default, default, 450, 5,
        new MagVector(1, 2, 3), new MagVector(1.1, 0.9, 1),
        new MagVector(0.01, 0.02, 0.03), 1, true);

    IReadOnlyList<(string Name, double Value)> writes =
        OfflineMagFitViewModel.BuildWrites([result], port);

    Assert.Equal(names, writes.Select(item => item.Name));
    Assert.Equal(new[] { 1d, 2d, 3d }, writes.Skip(1).Take(3).Select(item => item.Value));

    port.MAV.param.Remove(port.MAV.param["COMPASS_ODI2_Z"]);
    Assert.Throws<InvalidOperationException>(() =>
        OfflineMagFitViewModel.BuildWrites([result], port));
  }

  [Fact]
  public void Tlog_reads_second_and_third_scaled_imu_compasses() {
    var second = new MagVector(-15, 24, 11);
    var third = new MagVector(21, 9, -19);
    using var log = Tlog.Create();
    for (int index = 0; index < 240; index++) {
      log.WriteScaled(2, SpherePoint(index, 240, 390, second));
      log.WriteScaled(3, SpherePoint(index, 240, 440, third));
    }

    OfflineMagFitReport report = OfflineMagFitService.Analyze(log.Path, ellipsoid: false);

    Assert.Equal(new[] { 2, 3 }, report.Results.Select(result => result.Compass));
    AssertVector(second, report.Results[0].Offsets, 3.0);
    AssertVector(third, report.Results[1].Offsets, 3.0);
    Assert.All(report.Results, result => Assert.Equal(default, result.LoggedOffsets));
  }

  private static IEnumerable<MagVector> Sphere(
      int count, double radius, MagVector offsets) {
    for (int index = 0; index < count; index++) {
      yield return SpherePoint(index, count, radius, offsets);
    }
  }

  private static MagVector SpherePoint(
      int index, int count, double radius, MagVector offsets) {
    double y = 1 - 2 * (index + 0.5) / count;
    double radial = Math.Sqrt(Math.Max(0, 1 - y * y));
    double phi = index * Math.PI * (3 - Math.Sqrt(5));
    return new MagVector(
        radius * Math.Cos(phi) * radial - offsets.X,
        radius * y - offsets.Y,
        radius * Math.Sin(phi) * radial - offsets.Z);
  }

  private static string MagLine(long time, int instance, MagVector corrected, MagVector old) =>
      string.Create(CultureInfo.InvariantCulture,
          $"MAG, {time}, {instance}, {corrected.X + old.X}, {corrected.Y + old.Y}, "
          + $"{corrected.Z + old.Z}, {old.X}, {old.Y}, {old.Z}");

  private static void AssertVector(MagVector expected, MagVector actual, double tolerance) {
    Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
    Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
    Assert.InRange(actual.Z, expected.Z - tolerance, expected.Z + tolerance);
  }

  private sealed class TextLog : IDisposable {
    private readonly string _root;
    internal string Path { get; }

    private TextLog(string root, string path) {
      _root = root;
      Path = path;
    }

    internal static TextLog Create(IEnumerable<string> lines) => CreateCore(
        "FMT, 130, 41, MAG, QBffffff, TimeUS,I,MagX,MagY,MagZ,OfsX,OfsY,OfsZ", lines);

    internal static TextLog CreateLegacy(IEnumerable<string> lines) => CreateCore(
        "FMT, 131, 32, MAG2, Qfff, TimeUS,MagX,MagY,MagZ", lines);

    private static TextLog CreateCore(string format, IEnumerable<string> lines) {
      string root = System.IO.Path.Combine(
          System.IO.Path.GetTempPath(), "mp-magfit-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      string path = System.IO.Path.Combine(root, "test.log");
      File.WriteAllText(path, string.Join('\n', new[] {
        "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
        format,
      }.Concat(lines)) + "\n");
      return new TextLog(root, path);
    }

    public void Dispose() {
      try {
        Directory.Delete(_root, recursive: true);
      } catch {
      }
    }
  }

  private sealed class Tlog : IDisposable {
    private readonly string _root;
    private readonly FileStream _stream;
    private long _ticks;
    internal string Path { get; }

    private Tlog(string root, string path) {
      _root = root;
      Path = path;
      _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    internal static Tlog Create() {
      string root = System.IO.Path.Combine(
          System.IO.Path.GetTempPath(), "mp-magfit-tlog-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      return new Tlog(root, System.IO.Path.Combine(root, "test.tlog"));
    }

    internal void Write(MAVLink.MAVLINK_MSG_ID id, object payload) {
      ulong microseconds = (ulong)((DateTime.UnixEpoch.AddMilliseconds(_ticks++)
          - DateTime.UnixEpoch).Ticks / 10);
      byte[] stamp = BitConverter.GetBytes(microseconds);
      if (BitConverter.IsLittleEndian) {
        Array.Reverse(stamp);
      }
      _stream.Write(stamp);
      var parser = new MAVLink.MavlinkParse();
      _stream.Write(parser.GenerateMAVLinkPacket20(id, payload, false, 1, 1));
      _stream.Flush();
    }

    internal void WriteRaw(MagVector corrected, MagVector logged) => Write(
        MAVLink.MAVLINK_MSG_ID.RAW_IMU,
        new MAVLink.mavlink_raw_imu_t(
            (ulong)_ticks * 1000, 0, 0, 0, 0, 0, 0,
            (short)Math.Round(corrected.X + logged.X),
            (short)Math.Round(corrected.Y + logged.Y),
            (short)Math.Round(corrected.Z + logged.Z), 0, 0));

    internal void WriteScaled(int compass, MagVector point) {
      short x = (short)Math.Round(point.X);
      short y = (short)Math.Round(point.Y);
      short z = (short)Math.Round(point.Z);
      object packet = compass switch {
        2 => new MAVLink.mavlink_scaled_imu2_t(
            (uint)_ticks, 0, 0, 0, 0, 0, 0, x, y, z, 0),
        3 => new MAVLink.mavlink_scaled_imu3_t(
            (uint)_ticks, 0, 0, 0, 0, 0, 0, x, y, z, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(compass)),
      };
      Write(compass == 2 ? MAVLink.MAVLINK_MSG_ID.SCALED_IMU2 : MAVLink.MAVLINK_MSG_ID.SCALED_IMU3,
          packet);
    }

    public void Dispose() {
      _stream.Dispose();
      try {
        Directory.Delete(_root, recursive: true);
      } catch {
      }
    }
  }
}
